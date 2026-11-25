import { useEffect, useState } from "react";

interface WelcomeBuddyIconProps {
  className?: string;
  alt?: string;
  /** 0 = Monday … 6 = Sunday. Omit to follow the local weekday. */
  dayIndex?: number;
}

const FACE = "#D4E8FC";
const INK = "#2E3540";

const FACE_NAMES = ["微笑", "眨眼", "开心", "好奇", "得意", "困倦", "惊讶"] as const;

/** Monday = 0 … Sunday = 6. */
export function welcomeBuddyDayIndex(date = new Date()) {
  return (date.getDay() + 6) % 7;
}

function msUntilNextLocalMidnight(now = new Date()) {
  const next = new Date(now);
  next.setHours(24, 0, 0, 0);
  return Math.max(1000, next.getTime() - now.getTime() + 50);
}

/** Pale-blue welcome face. One of seven expressions, swapped each local weekday. */
export function WelcomeBuddyIcon({
  className = "h-11 w-11",
  alt,
  dayIndex,
}: WelcomeBuddyIconProps) {
  const [index, setIndex] = useState(() => dayIndex ?? welcomeBuddyDayIndex());

  useEffect(() => {
    if (dayIndex !== undefined) {
      setIndex(((dayIndex % 7) + 7) % 7);
      return;
    }

    let timer = 0;
    const arm = () => {
      setIndex(welcomeBuddyDayIndex());
      timer = window.setTimeout(arm, msUntilNextLocalMidnight());
    };
    arm();
    return () => window.clearTimeout(timer);
  }, [dayIndex]);

  const name = FACE_NAMES[index] ?? FACE_NAMES[0];

  return (
    <svg
      className={className}
      viewBox="0 0 64 64"
      role="img"
      aria-label={alt ?? `Athlon ${name}`}
      xmlns="http://www.w3.org/2000/svg"
    >
      <circle cx="32" cy="32" r="32" fill={FACE} />
      <FaceMarks index={index} />
    </svg>
  );
}

function FaceMarks({ index }: { index: number }) {
  switch (index) {
    case 1:
      return (
        <>
          <rect x="26.5" y="15.4" width="6.3" height="11.8" rx="3.15" fill={INK} />
          <rect x="43.6" y="15.2" width="7.4" height="8.6" rx="3.7" fill={INK} />
          <path
            d="M18 31.4C22.6 44.2 33.2 46.6 41.2 38.4"
            fill="none"
            stroke={INK}
            strokeWidth="5.3"
            strokeLinecap="round"
          />
        </>
      );
    case 2:
      return (
        <>
          <path
            d="M20.4 26.2C23.6 21 26.8 21 30 26.2"
            fill="none"
            stroke={INK}
            strokeWidth="4.7"
            strokeLinecap="round"
          />
          <path
            d="M34 26.2C37.2 21 40.4 21 43.6 26.2"
            fill="none"
            stroke={INK}
            strokeWidth="4.7"
            strokeLinecap="round"
          />
          <path
            d="M17.5 34C24.5 50 39.5 50 46.5 34"
            fill="none"
            stroke={INK}
            strokeWidth="5.2"
            strokeLinecap="round"
          />
        </>
      );
    case 3:
      return (
        <>
          <path
            d="M36.4 13.2C38.6 10.6 42.8 10.8 44.6 13.8"
            fill="none"
            stroke={INK}
            strokeWidth="3.3"
            strokeLinecap="round"
          />
          <rect x="21.4" y="19" width="6.2" height="10.4" rx="3.1" fill={INK} />
          <rect x="36.6" y="16" width="6.2" height="10.4" rx="3.1" fill={INK} />
          <path
            d="M22.5 37.4C27.6 42.6 36.4 43.4 43.4 36"
            fill="none"
            stroke={INK}
            strokeWidth="4.8"
            strokeLinecap="round"
          />
        </>
      );
    case 4:
      return (
        <>
          <rect x="20.8" y="16.8" width="6.3" height="11.2" rx="3.15" fill={INK} />
          <path
            d="M35.2 25.6C38.67 20.67 42.13 20.27 45.6 24.4"
            fill="none"
            stroke={INK}
            strokeWidth="4.7"
            strokeLinecap="round"
          />
          <path
            d="M19.5 36.2C26.5 41.6 37.2 46.4 46.4 33"
            fill="none"
            stroke={INK}
            strokeWidth="5"
            strokeLinecap="round"
          />
        </>
      );
    case 5:
      return (
        <>
          <rect x="19.4" y="22.8" width="11.6" height="5.2" rx="2.6" fill={INK} />
          <rect x="33" y="22.8" width="11.6" height="5.2" rx="2.6" fill={INK} />
          <path
            d="M25 38.4C28.4 42.4 35.6 42.4 39 38.4"
            fill="none"
            stroke={INK}
            strokeWidth="4.6"
            strokeLinecap="round"
          />
        </>
      );
    case 6:
      return (
        <>
          <circle cx="25.2" cy="23.2" r="4.5" fill={INK} />
          <circle cx="38.8" cy="23.2" r="4.5" fill={INK} />
          <ellipse cx="32" cy="40.2" rx="4.4" ry="5.6" fill={INK} />
        </>
      );
    default:
      return (
        <>
          <rect x="22" y="16.4" width="6.4" height="11.4" rx="3.2" fill={INK} />
          <rect x="35.6" y="16.4" width="6.4" height="11.4" rx="3.2" fill={INK} />
          <path
            d="M20 35.2C26.2 46.4 37.8 46.4 44 35.2"
            fill="none"
            stroke={INK}
            strokeWidth="5"
            strokeLinecap="round"
          />
        </>
      );
  }
}
