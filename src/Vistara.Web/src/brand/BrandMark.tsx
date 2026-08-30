interface BrandMarkProps {
  readonly className?: string;
  /** Set when the mark is the only content of a link or button. */
  readonly title?: string;
}

/**
 * The Vistara mark: a framed vista with a low sun over two ridges. The same
 * geometry is rasterised by `scripts/brand-assets.mjs` for the installable
 * icons, so the application and the launcher always agree.
 */
export function BrandMark({ className, title }: BrandMarkProps) {
  return (
    <svg
      className={className}
      viewBox="0 0 64 64"
      {...(title
        ? { role: 'img', 'aria-label': title }
        : { 'aria-hidden': 'true', focusable: 'false' })}
    >
      {title ? <title>{title}</title> : null}
      <defs>
        <clipPath id="vistara-mark-frame">
          <rect x="0" y="0" width="64" height="64" rx="14" />
        </clipPath>
      </defs>
      <g clipPath="url(#vistara-mark-frame)">
        <rect width="64" height="64" fill="var(--color-surface-raised)" />
        <circle cx="44.8" cy="19.2" r="7.7" fill="var(--color-focus)" />
        <path d="M1.3 49.9 25.6 16.6 49.9 49.9Z" fill="var(--color-accent-strong)" />
        <path d="M19.2 49.9 41 28.2 62.7 49.9Z" fill="var(--color-accent)" />
        <rect x="0" y="49.9" width="64" height="14.1" fill="var(--color-accent-soft)" />
      </g>
      <rect
        x="0.5"
        y="0.5"
        width="63"
        height="63"
        rx="13.5"
        fill="none"
        stroke="var(--color-border)"
      />
    </svg>
  );
}
