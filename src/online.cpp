// Online play between friends over a LAN or a virtual LAN (Radmin VPN, ZeroTier,
// Hamachi), on top of the SDK's direct XNet mode (rexglue-sdk-dbz, xam_net.cpp
// and apps/xgi_app.cpp, driven by the REX_XNET_* environment variables).
//
// With rewor_online on, the game sets those variables from the re-wor cvars
// before it starts, and answers the few calls the SDK leaves as "offline" for
// Warriors of Rock to go into Xbox LIVE matchmaking: the network cable is
// plugged in, Xbox LIVE knows the title, the profile is signed in to LIVE and
// every PC has its own XUID (the SDK gives every PC the same one).
//
// The host leaves rewor_online_host empty and opens matchmaking first; the
// friend puts the host's address in rewor_online_host and finds that match.
//
// What Warriors of Rock needs online was worked out by Piscacho for his PC
// port (https://github.com/Piscacho/Guitar-Hero-Warriors-of-Rock-PC,
// runtime/net.cpp and NOTAS_SESION24.md, session 27), MIT License,
// Copyright (c) 2026 Piscacho.

#include "generated/rewor_init.h"

#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/function.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xam/user_profile.h>

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <windows.h>
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")
#endif

REXCVAR_DEFINE_BOOL(rewor_online, false, "re-wor",
                    "Online play with friends over a LAN or a virtual LAN (Radmin VPN, ZeroTier, Hamachi)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_online_ip, "", "re-wor",
                      "This PC's address on the LAN or virtual LAN; empty = found automatically "
                      "(Radmin 26.x, Hamachi 25.x, then the local network)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
REXCVAR_DEFINE_STRING(rewor_online_host, "", "re-wor",
                      "The host's address, to join the host's match; empty = this PC hosts")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

namespace {

constexpr uint32_t kTitleId = 0x41560883;
constexpr uint32_t kSuccess = 0;
constexpr uint32_t kInvalidArg = 0x80070057;
constexpr uint32_t kNoSuchUser = 0x80070525;

bool g_online = false;
uint64_t g_xuid = 0;

void Store32(uint8_t* base, uint32_t a, uint32_t v) {
  base[a] = uint8_t(v >> 24);
  base[a + 1] = uint8_t(v >> 16);
  base[a + 2] = uint8_t(v >> 8);
  base[a + 3] = uint8_t(v);
}
void Store64(uint8_t* base, uint32_t a, uint64_t v) {
  Store32(base, a, uint32_t(v >> 32));
  Store32(base, a + 4, uint32_t(v));
}

bool ParseIpv4(const std::string& text, uint32_t* out) {
  in_addr addr = {};
  if (inet_pton(AF_INET, text.c_str(), &addr) != 1) return false;
  *out = ntohl(addr.s_addr);
  return true;
}

std::string Ipv4Text(uint32_t ip) {
  return std::to_string(ip >> 24) + "." + std::to_string((ip >> 16) & 0xFF) + "." +
         std::to_string((ip >> 8) & 0xFF) + "." + std::to_string(ip & 0xFF);
}

// Lower is better: Radmin VPN, Hamachi, other private networks (ZeroTier, home
// LAN), anything else. Loopback and link-local addresses are never chosen.
int AddressRank(uint32_t ip) {
  const uint32_t a = ip >> 24, b = (ip >> 16) & 0xFF;
  if (a == 127 || (a == 169 && b == 254) || a == 0) return 100;
  if (a == 26) return 0;
  if (a == 25) return 1;
  if (a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168)) return 2;
  return 3;
}

uint32_t FindLocalAddress() {
  uint32_t best = 0;
  int best_rank = 100;
#ifdef _WIN32
  ULONG size = 16 * 1024;
  std::string buffer(size, '\0');
  auto* list = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
  const ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
  ULONG result = GetAdaptersAddresses(AF_INET, flags, nullptr, list, &size);
  if (result == ERROR_BUFFER_OVERFLOW) {
    buffer.assign(size, '\0');
    list = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
    result = GetAdaptersAddresses(AF_INET, flags, nullptr, list, &size);
  }
  if (result != NO_ERROR) return 0;
  for (auto* adapter = list; adapter; adapter = adapter->Next) {
    if (adapter->OperStatus != IfOperStatusUp) continue;
    for (auto* unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
      const auto* sa = reinterpret_cast<const sockaddr_in*>(unicast->Address.lpSockaddr);
      if (!sa || sa->sin_family != AF_INET) continue;
      const uint32_t ip = ntohl(sa->sin_addr.s_addr);
      const int rank = AddressRank(ip);
      if (rank < best_rank) {
        best_rank = rank;
        best = ip;
      }
    }
  }
#endif
  return best;
}

// 0x0009 + 48 bits from the computer name and the address, so two PCs (or two
// copies on one PC, with different loopback addresses) never share an XUID.
uint64_t MakeXuid(uint32_t ip) {
  std::string seed = "re-wor|" + Ipv4Text(ip) + "|";
#ifdef _WIN32
  char name[MAX_COMPUTERNAME_LENGTH + 1] = {};
  DWORD length = sizeof(name);
  if (GetComputerNameA(name, &length)) seed += name;
#endif
  uint64_t hash = 0xCBF29CE484222325ull;
  for (unsigned char c : seed) hash = (hash ^ c) * 0x100000001B3ull;
  return 0x0009000000000000ull | (hash & 0x0000FFFFFFFFFFFFull);
}

void SetEnv(const char* name, const std::string& value) {
  _putenv_s(name, value.c_str());
}

}  // namespace

// Called before the game starts (ReWorApp::OnPreLaunchModule, src/main.cpp).
// The SDK reads the REX_XNET_* variables when the game first uses the network,
// which is later.
void ReWorStartOnline() {
  if (!REXCVAR_GET(rewor_online)) return;

  uint32_t ip = 0;
  const std::string configured = REXCVAR_GET(rewor_online_ip);
  if (!configured.empty() && !ParseIpv4(configured, &ip)) {
    REXLOG_ERROR("[online] rewor_online_ip is not an IPv4 address: {}", configured);
    return;
  }
  if (!ip) ip = FindLocalAddress();
  if (!ip) {
    REXLOG_ERROR("[online] no network address found; online play stays off");
    return;
  }

  uint32_t host = 0;
  const std::string host_text = REXCVAR_GET(rewor_online_host);
  if (!host_text.empty() && !ParseIpv4(host_text, &host)) {
    REXLOG_ERROR("[online] rewor_online_host is not an IPv4 address: {}", host_text);
    return;
  }

  // The direct (LAN / VPN) path of the SDK, never its lobby server.
  SetEnv("REX_XNET_IP", Ipv4Text(ip));
  SetEnv("REX_XNET_BIND_IP", Ipv4Text(ip));
  if (host) SetEnv("REX_XNET_SEARCH_IP", Ipv4Text(host));

  g_xuid = MakeXuid(ip);
  g_online = true;
  // The SDK logs every network and session call at warning level; the launcher
  // sets log_level to "error", which would hide them. Only these two
  // categories are raised, so the rest of the log stays quiet.
  rex::SetCategoryLevel(rex::log::krnl(), spdlog::level::warn);
  rex::SetCategoryLevel(rex::log::core(), spdlog::level::warn);
  REXLOG_WARN("[online] on: this PC {} ({}), xuid {:016X}", Ipv4Text(ip),
              host ? "joins " + Ipv4Text(host) : std::string("hosts"), g_xuid);
}

// XNetGetEthernetLinkStatus(): 0x0B = link active, 100 Mbps, full duplex. The
// SDK always answers 0 (no cable), and the game then waits for a connection.
REX_HOOK_RAW(__imp__NetDll_XNetGetEthernetLinkStatus) {
  (void)base;
  ctx.r3.u64 = g_online ? 0x0B : 0;
}

// XNetConnect(caller, address) and XNetGetConnectStatus(caller, address): the
// SDK leaves both as stubs that return nothing, and the game waits for status
// 2 (connected) before it talks to the other player. The direct mode has no
// key exchange to wait for, so a peer is connected as soon as it is asked for.
REX_HOOK_RAW(__imp__NetDll_XNetConnect) {
  (void)base;
  if (!g_online) return;
  REXLOG_WARN("[online] XNetConnect {:08X}", ctx.r4.u32);
  ctx.r3.u64 = 0;
}
REX_HOOK_RAW(__imp__NetDll_XNetGetConnectStatus) {
  (void)base;
  if (!g_online) return;
  static int logged = 0;
  if (logged < 10) {
    ++logged;
    REXLOG_WARN("[online] XNetGetConnectStatus {:08X} -> connected", ctx.r4.u32);
  }
  ctx.r3.u64 = 2;
}

// XNetQosListen(caller, session id, data, size, bits per second, flags): the
// host announces its match with it. The SDK fails it outside its lobby mode.
REX_HOOK_RAW(__imp__NetDll_XNetQosListen) {
  (void)base;
  REXLOG_WARN("[online] XNetQosListen data {:08X} size {} flags {:X}", ctx.r5.u32, ctx.r6.u32,
              ctx.r8.u32);
  ctx.r3.u64 = g_online ? 0 : 0x65B;
}

// XNetLogonGetTitleID(): the title Xbox LIVE is logged on with.
REX_HOOK_RAW(__imp__XNetLogonGetTitleID) {
  (void)base;
  ctx.r3.u64 = g_online ? kTitleId : 0;
}

// XamUserGetXUID(user, type mask, &xuid): as the SDK answers it, with this PC's
// own XUID online. Saves keep using the profile's XUID (XamContentGetCreator).
REX_HOOK_RAW(__imp__XamUserGetXUID) {
  const uint32_t user = ctx.r3.u32, out = ctx.r5.u32;
  if (!out) {
    ctx.r3.u64 = kInvalidArg;
    return;
  }
  uint64_t xuid = 0;
  uint32_t result = kNoSuchUser;
  if (user >= 4) {
    result = kInvalidArg;
  } else {
    const auto& profile = REX_KERNEL_STATE()->user_profile(user);
    if (profile->IsSignedIn(user)) {
      xuid = g_online ? g_xuid + profile->index() : profile->xuid();
      result = kSuccess;
    }
  }
  Store64(base, out, xuid);
  ctx.r3.u64 = result;
}

// XamUserGetSigninInfo(user, flags, &info): XUSER_SIGNIN_INFO is
// { u64 xuid, u32, u32 state, u32, u32, char name[16] } (40 bytes). Online, the
// state is 2 (signed in to Xbox LIVE), as XamUserGetSigninState already says.
REX_HOOK_RAW(__imp__XamUserGetSigninInfo) {
  const uint32_t user = ctx.r3.u32, info = ctx.r5.u32;
  if (!info) {
    ctx.r3.u64 = kInvalidArg;
    return;
  }
  std::memset(base + info, 0, 40);
  const auto& profile = REX_KERNEL_STATE()->user_profile(user);
  if (user >= 4 || !profile->IsSignedIn(user)) {
    ctx.r3.u64 = kNoSuchUser;
    return;
  }
  Store64(base, info, g_online ? g_xuid + profile->index() : profile->xuid());
  Store32(base, info + 12, g_online ? 2 : profile->signin_state());
  const std::string name = profile->name();
  std::memcpy(base + info + 24, name.data(), std::min<size_t>(name.size(), 15));
  ctx.r3.u64 = kSuccess;
}
