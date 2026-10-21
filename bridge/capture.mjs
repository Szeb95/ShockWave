// PiCtrl's OSC sender exposes three Int parameters. A fresh Action=0 arms
// the bridge; a later 0 -> 1/2/3 transition captures the two value parameters.
export class OscActionCapture {
  constructor(onAction, settleMs = 100) {
    this.onAction = onAction;
    this.settleMs = settleMs;
    this.intensity = null;
    this.duration = null;
    this.lastAction = null;
    this.armed = false;
    this.timers = new Set();
  }

  setIntensity(value) {
    if (Number.isInteger(value) && value >= 0 && value <= 100) this.intensity = value;
  }

  setDuration(value) {
    if (Number.isInteger(value) && value >= 0 && value <= 100) this.duration = value;
  }

  setAction(value) {
    if (!Number.isInteger(value) || value < 0 || value > 3) return;
    if (value === 0) {
      this.armed = true;
      this.lastAction = 0;
      return;
    }
    const shouldCapture = this.armed && this.lastAction === 0;
    this.armed = false;
    this.lastAction = value;
    if (!shouldCapture) return;
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      this.onAction({
        type: value === 1 ? 'Shock' : value === 2 ? 'Vibrate' : 'Sound',
        intensity: this.intensity,
        duration: this.duration,
      });
    }, this.settleMs);
    this.timers.add(timer);
  }

  close() {
    for (const timer of this.timers) clearTimeout(timer);
    this.timers.clear();
  }
}
