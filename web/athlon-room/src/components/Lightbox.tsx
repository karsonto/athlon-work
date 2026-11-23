import { X } from "lucide-react";

interface LightboxProps {
  src: string;
  alt?: string;
  onClose: () => void;
}

export function Lightbox({ src, alt, onClose }: LightboxProps) {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/85 p-4"
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      onKeyDown={(e) => {
        if (e.key === "Escape") onClose();
      }}
    >
      <button
        type="button"
        className="absolute top-4 right-4 rounded-full bg-white/15 p-2 text-white hover:bg-white/25"
        onClick={onClose}
        aria-label="关闭预览"
      >
        <X size={22} />
      </button>
      <img
        src={src}
        alt={alt ?? "预览"}
        className="max-h-[92vh] max-w-[96vw] object-contain"
        onClick={(e) => e.stopPropagation()}
      />
    </div>
  );
}
