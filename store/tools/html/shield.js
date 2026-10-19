"use strict";

// The WebFilter mark: a shield with a check. One source for every icon, logo and promo image.
// variant "color": blue shield, white check (icons, logo). variant "inverse": white shield, blue check (on brand backgrounds).
window.shieldSvg = function shieldSvg({ size, variant = "color" }) {
  const gradientId = "wf-shield-" + Math.random().toString(36).slice(2);
  const fill = variant === "inverse" ? "#ffffff" : `url(#${gradientId})`;
  const check = variant === "inverse" ? "#1d5fd1" : "#ffffff";
  // Thicker check at toolbar sizes so it stays legible at 16-32 px.
  const checkWidth = size <= 32 ? 12.5 : 10;
  return `
<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 100 100" style="display:block">
  <defs>
    <linearGradient id="${gradientId}" x1="0" y1="0" x2="0.35" y2="1">
      <stop offset="0" stop-color="#3d8bf2" />
      <stop offset="1" stop-color="#1546b3" />
    </linearGradient>
  </defs>
  <path d="M50 6.5 L85 19 V46.5 C85 69 70 85 50 93.5 C30 85 15 69 15 46.5 V19 Z"
        fill="${fill}" stroke="${fill}" stroke-width="5" stroke-linejoin="round" />
  <path d="M33 50.5 L45 62.5 L67.5 39.5" fill="none" stroke="${check}" stroke-width="${checkWidth}"
        stroke-linecap="round" stroke-linejoin="round" />
</svg>`;
};
