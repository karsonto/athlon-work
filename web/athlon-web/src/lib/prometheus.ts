export type PromSample = {
  name: string;
  labels: Record<string, string>;
  value: number;
  timestamp?: number;
};

function parseLabelSet(s: string): Record<string, string> {
  const out: Record<string, string> = {};
  let i = 0;
  while (i < s.length) {
    while (i < s.length && (s[i] === "," || s[i] === " ")) {
      i += 1;
    }
    if (i >= s.length) {
      break;
    }
    const eq = s.indexOf("=", i);
    if (eq === -1) {
      break;
    }
    const key = s.slice(i, eq).trim();
    i = eq + 1;
    if (s[i] === '"') {
      i += 1;
      let val = "";
      while (i < s.length) {
        if (s[i] === "\\" && i + 1 < s.length) {
          val += s[i + 1];
          i += 2;
        } else if (s[i] === '"') {
          i += 1;
          break;
        } else {
          val += s[i];
          i += 1;
        }
      }
      out[key] = val;
    } else {
      let end = i;
      while (end < s.length && s[end] !== ",") {
        end += 1;
      }
      out[key] = s.slice(i, end).trim();
      i = end;
    }
  }
  return out;
}

function parseLine(line: string): PromSample | null {
  const trimmed = line.trim();
  if (!trimmed || trimmed.startsWith("#")) {
    return null;
  }

  let i = 0;
  const nameStart = i;
  while (i < trimmed.length && trimmed[i] !== "{" && trimmed[i] !== " " && trimmed[i] !== "\t") {
    i += 1;
  }
  const metricName = trimmed.slice(nameStart, i).trim();
  if (!metricName) {
    return null;
  }

  let labels: Record<string, string> = {};
  if (i < trimmed.length && trimmed[i] === "{") {
    const close = trimmed.indexOf("}", i);
    if (close === -1) {
      return null;
    }
    labels = parseLabelSet(trimmed.slice(i + 1, close));
    i = close + 1;
  }

  const remainder = trimmed.slice(i).trim();
  const tokens = remainder.split(/\s+/).filter(Boolean);
  if (tokens.length < 1) {
    return null;
  }
  const valueStr = tokens[0];
  const value = Number(valueStr);
  if (Number.isNaN(value)) {
    return null;
  }
  const timestamp =
    tokens.length > 1 && /^\d+$/.test(tokens[1]) ? Number(tokens[1]) : undefined;

  return { name: metricName, labels, value, timestamp };
}

export function parsePrometheusText(text: string): PromSample[] {
  const samples: PromSample[] = [];
  for (const line of text.split(/\r?\n/)) {
    const row = parseLine(line);
    if (row) {
      samples.push(row);
    }
  }
  return samples;
}

function matchesLabels(sample: PromSample, labelFilter?: Record<string, string>): boolean {
  if (!labelFilter || Object.keys(labelFilter).length === 0) {
    return true;
  }
  return Object.entries(labelFilter).every(([k, v]) => sample.labels[k] === v);
}

export function findSamples(
  samples: PromSample[],
  name: string,
  labelFilter?: Record<string, string>,
): PromSample[] {
  return samples.filter((s) => s.name === name && matchesLabels(s, labelFilter));
}

export function firstValue(
  samples: PromSample[],
  name: string,
  labelFilter?: Record<string, string>,
): number | undefined {
  const found = findSamples(samples, name, labelFilter);
  return found[0]?.value;
}

export function sumMetricValues(
  samples: PromSample[],
  name: string,
  labelFilter?: Record<string, string>,
): number {
  return findSamples(samples, name, labelFilter).reduce((acc, s) => acc + s.value, 0);
}

export function histogramMean(
  samples: PromSample[],
  baseName: string,
  labelFilter?: Record<string, string>,
): number | undefined {
  const sums = findSamples(samples, `${baseName}_sum`, labelFilter);
  const counts = findSamples(samples, `${baseName}_count`, labelFilter);
  const sumVal = sums.reduce((a, s) => a + s.value, 0);
  const countVal = counts.reduce((a, s) => a + s.value, 0);
  if (countVal === 0) {
    return undefined;
  }
  return sumVal / countVal;
}

export function collectLabelValues(
  samples: PromSample[],
  metricName: string,
  labelKey: string,
): string[] {
  const set = new Set<string>();
  for (const s of samples) {
    if (s.name === metricName) {
      const v = s.labels[labelKey];
      if (v !== undefined && v !== "") {
        set.add(v);
      }
    }
  }
  return [...set].sort();
}
