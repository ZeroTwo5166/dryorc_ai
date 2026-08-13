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
