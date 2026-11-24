interface AthlonIconProps {
  className?: string;
  alt?: string;
}

export function AthlonIcon({ className = "h-10 w-10", alt = "Athlon" }: AthlonIconProps) {
  return (
    <img
      src="/athlon-icon.svg"
      alt={alt}
      className={className}
      draggable={false}
    />
  );
}
