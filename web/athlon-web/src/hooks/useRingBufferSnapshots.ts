import { useCallback, useState } from "react";

/** 追加快照并截断为最多 maxLen 条（环形缓冲语义）。 */
export function useRingBufferSnapshots<T>(maxLen: number): [T[], (entry: T) => void] {
  const [items, setItems] = useState<T[]>([]);
  const push = useCallback(
    (entry: T) => {
      setItems((prev) => {
        const next = [...prev, entry];
        if (next.length > maxLen) {
          return next.slice(next.length - maxLen);
        }
        return next;
      });
    },
    [maxLen],
  );
  return [items, push];
}
