// The game's clock and the music.
//
// The game measures time with the PowerPC timebase, divided by the frequency
// the kernel reports. A real Xbox 360 reports 50,000,000 Hz while its timebase
// really runs at 49,875,000 Hz, so the game's timer setup (sub_823A0238) has a
// correction: "if the kernel says 50,000,000, use 49,875,000".
//
// The SDK's timebase does run at the 50,000,000 Hz it reports. With the
// correction the game's clock ran 0.25 % fast: the notes got ahead of the
// music, which plays at the sound card's own rate, by about 150 ms a minute.
// The hook makes the comparison fail, so the game keeps the reported frequency.

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>

REXCVAR_DEFINE_BOOL(rewor_timer_fix, true, "re-wor",
                    "Keep the notes in time with the music: the game's clock uses the timer "
                    "frequency the system reports instead of the console's 49.875 MHz")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

// Midasm hook at 0x823A02D4, "cmplw cr6,r11,r7": r11 is the reported frequency
// and r7 is 50,000,000. r7 is not read again before it is overwritten.
void ReWorTimerFrequencyHook(PPCRegister& r7) {
  if (!REXCVAR_GET(rewor_timer_fix)) return;
  REXLOG_INFO("[timer] game clock at the reported frequency (rewor_timer_fix)");
  r7.u32 = 0;
}
