export function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '0 B';
  }

  const units = ['B', 'kB', 'MB', 'GB', 'TB', 'PB'];
  const exponent = Math.min(units.length - 1, Math.floor(Math.log10(value) / 3));
  const scaled = value / 1000 ** exponent;
  const formatted = new Intl.NumberFormat(undefined, {
    maximumFractionDigits: scaled >= 100 || exponent === 0 ? 0 : 1,
  }).format(scaled);

  return `${formatted} ${units[exponent]}`;
}

export function formatMoment(value: string | undefined): string {
  if (!value) {
    return '—';
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? '—'
    : new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(date);
}
