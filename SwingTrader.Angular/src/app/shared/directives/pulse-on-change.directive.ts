import { Directive, ElementRef, effect, input } from '@angular/core';

// Briefly highlights an element when its bound value changes - used on the
// dashboard stat values so poll refreshes read as a live tick instead of a
// silent jump. Pure CSS animation restart; no change-detection cost.
@Directive({
  selector: '[appPulseOnChange]',
  standalone: true,
})
export class PulseOnChangeDirective {
  appPulseOnChange = input.required<unknown>();

  private first = true;

  constructor(private el: ElementRef<HTMLElement>) {
    effect(() => {
      this.appPulseOnChange(); // track
      if (this.first) {
        this.first = false;
        return;
      }
      const node = this.el.nativeElement;
      node.classList.remove('value-pulse');
      // Force reflow so re-adding the class restarts the animation.
      void node.offsetWidth;
      node.classList.add('value-pulse');
    });
  }
}
