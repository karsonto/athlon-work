interface WelcomeBuddyIconProps {
  className?: string;
  alt?: string;
}

/** Soft circular welcome mark — cute & simple; original artwork. */
export function WelcomeBuddyIcon({
  className = "h-11 w-11",
  alt = "Athlon",
}: WelcomeBuddyIconProps) {
  return (
    <svg
      className={className}
      viewBox="0 0 64 64"
      role="img"
      aria-label={alt}
      xmlns="http://www.w3.org/2000/svg"
    >
      <circle cx="32" cy="32" r="32" fill="#A9DCF2" />
      <circle cx="24.5" cy="29" r="3.2" fill="#0B6FA4" />
      <circle cx="39.5" cy="29" r="3.2" fill="#0B6FA4" />
      <circle cx="25.4" cy="28.2" r="1.05" fill="#E8F7FD" />
      <circle cx="40.4" cy="28.2" r="1.05" fill="#E8F7FD" />
      <path
        d="M24 37.5c2.4 3.4 5.6 5.1 8 5.1s5.6-1.7 8-5.1"
        fill="none"
        stroke="#0B6FA4"
        strokeWidth="2.6"
        strokeLinecap="round"
      />
      <circle cx="18.5" cy="35.5" r="2.1" fill="#7EC8E8" opacity="0.85" />
      <circle cx="45.5" cy="35.5" r="2.1" fill="#7EC8E8" opacity="0.85" />
    </svg>
  );
}
