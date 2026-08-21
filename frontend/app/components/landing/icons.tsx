type IconProps = {
  className?: string;
};

const base = {
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.75,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  viewBox: "0 0 24 24",
};

export function IconLayers({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M12 3 3 8l9 5 9-5-9-5Z" />
      <path d="m3 13 9 5 9-5" />
    </svg>
  );
}

export function IconTarget({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="8.5" />
      <circle cx="12" cy="12" r="4.5" />
      <path d="M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21" />
    </svg>
  );
}

export function IconShieldCheck({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M12 3.5 5 6v6c0 4.2 3 7.4 7 8.5 4-1.1 7-4.3 7-8.5V6l-7-2.5Z" />
      <path d="m9.25 12 2 2 3.5-4" />
    </svg>
  );
}

export function IconUserCheck({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="9.5" cy="8" r="3.25" />
      <path d="M3.75 19c.6-3 2.9-5 5.75-5s5.15 2 5.75 5" />
      <path d="m15.5 12.5 1.75 1.75L20.5 11" />
    </svg>
  );
}

export function IconWorkflow({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <rect x="3" y="4" width="6" height="5" rx="1.25" />
      <rect x="15" y="15" width="6" height="5" rx="1.25" />
      <path d="M6 9v3a3 3 0 0 0 3 3h3" />
      <path d="M15 11h-2a3 3 0 0 0-3 3v.5" />
    </svg>
  );
}

export function IconLock({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <rect x="4.75" y="10.5" width="14.5" height="9.5" rx="2" />
      <path d="M7.5 10.5V7.25a4.5 4.5 0 0 1 9 0V10.5" />
    </svg>
  );
}

export function IconArrowRight({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M4.5 12h15" />
      <path d="m13 5.5 7 6.5-7 6.5" />
    </svg>
  );
}

export function IconCheck({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="m4.5 12.75 5 5 10-11" />
    </svg>
  );
}

export function IconStore({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M4 9.5 5.25 4h13.5L20 9.5" />
      <path d="M4 9.5a2.25 2.25 0 0 0 4.5 0 2.25 2.25 0 0 0 4.5 0 2.25 2.25 0 0 0 4.5 0 2.25 2.25 0 0 0 4.5 0" />
      <path d="M5.5 9.5V20h13V9.5" />
      <path d="M10 20v-5.5a2 2 0 0 1 2-2 2 2 0 0 1 2 2V20" />
    </svg>
  );
}

export function IconMessageSquare({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M4 5.5h16v10.5H9.5L5 20v-4H4V5.5Z" />
    </svg>
  );
}

export function IconScale({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M12 3v18M8 21h8" />
      <path d="M12 5.5 5 7l3 6.5a3.3 3.3 0 0 0 4 0L15 7l-3-1.5Z" />
      <path d="m5 7 3 6.5" />
      <path d="m19 7-3 6.5a3.3 3.3 0 0 0 4 0L23 13" />
    </svg>
  );
}

export function IconRadar({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 12 17 7" />
      <path d="M12 3.5A8.5 8.5 0 0 1 20.5 12" />
    </svg>
  );
}

export function IconMail({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <rect x="3.5" y="5.5" width="17" height="13" rx="2" />
      <path d="m4.5 7 7.5 6 7.5-6" />
    </svg>
  );
}

export function IconSend({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M20.5 3.5 3 10.75l6.5 2.75L20.5 3.5Z" />
      <path d="M20.5 3.5 13.25 20l-2.75-6.5L20.5 3.5Z" />
    </svg>
  );
}

export function IconHome({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M4 11.5 12 4l8 7.5" />
      <path d="M6 10v9.5h12V10" />
      <path d="M10 19.5V14h4v5.5" />
    </svg>
  );
}

export function IconSettings({ className }: IconProps) {
  // A gear, not a sunburst — this used to share IconSun's exact shape (a
  // circle plus thin radiating lines), which reads as a light/brightness
  // icon rather than settings. Six rounded-rect teeth around a ring instead.
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="5.25" />
      <circle cx="12" cy="12" r="2" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" transform="rotate(60 12 12)" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" transform="rotate(120 12 12)" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" transform="rotate(180 12 12)" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" transform="rotate(240 12 12)" />
      <rect x="10.8" y="3.75" width="2.4" height="3" rx="1" transform="rotate(300 12 12)" />
    </svg>
  );
}

export function IconChevronsLeft({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M15.5 5.5 9 12l6.5 6.5" />
      <path d="M10 5.5 3.5 12l6.5 6.5" />
    </svg>
  );
}

export function IconLogOut({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M9.5 4H6.75A2.25 2.25 0 0 0 4.5 6.25v11.5A2.25 2.25 0 0 0 6.75 20H9.5" />
      <path d="m15.5 16 4-4-4-4" />
      <path d="M19.25 12h-10.5" />
    </svg>
  );
}

export function IconClock({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 7.5V12l3.25 2" />
    </svg>
  );
}

export function IconEye({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M2.5 12S6 5.75 12 5.75 21.5 12 21.5 12 18 18.25 12 18.25 2.5 12 2.5 12Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

export function IconEyeOff({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M6.6 6.7C4.1 8.3 2.5 12 2.5 12s3.5 6.25 9.5 6.25c1.5 0 2.9-.4 4.1-1M14.6 6.85A9.9 9.9 0 0 0 12 5.75c6 0 9.5 6.25 9.5 6.25a15.4 15.4 0 0 1-1.9 2.75" />
      <path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" />
      <path d="M3.5 3.5l17 17" />
    </svg>
  );
}

export function IconSun({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="4.25" />
      <path d="M12 3v2.25M12 18.75V21M21 12h-2.25M5.25 12H3M18.02 5.98l-1.6 1.6M7.58 16.42l-1.6 1.6M18.02 18.02l-1.6-1.6M7.58 7.58l-1.6-1.6" />
    </svg>
  );
}

export function IconMoon({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M20.5 14.5A8.5 8.5 0 0 1 9.5 3.5a8.5 8.5 0 1 0 11 11Z" />
    </svg>
  );
}

export function IconMonitor({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <rect x="3" y="4.5" width="18" height="12" rx="1.75" />
      <path d="M8.5 20h7M12 16.5V20" />
    </svg>
  );
}

export function IconSearch({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="m20 20-4.35-4.35" />
    </svg>
  );
}

export function IconBell({ className }: IconProps) {
  return (
    <svg {...base} className={className} aria-hidden="true">
      <path d="M12 3.5a5.25 5.25 0 0 0-5.25 5.25v3.2L4.75 15.5h14.5L17 11.95v-3.2A5.25 5.25 0 0 0 12 3.5Z" />
      <path d="M10.1 19a1.9 1.9 0 0 0 3.8 0" />
    </svg>
  );
}
