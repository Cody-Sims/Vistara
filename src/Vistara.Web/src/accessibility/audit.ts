export type AccessibilityImpact =
  | 'critical'
  | 'serious'
  | 'moderate'
  | 'minor';

export interface AccessibilityFinding {
  rule: string;
  impact: AccessibilityImpact;
  message: string;
  element?: Element;
}

interface RgbColor {
  red: number;
  green: number;
  blue: number;
}

function parseHexColor(value: string): RgbColor {
  const normalized = value.trim().replace(/^#/, '');
  const expanded =
    normalized.length === 3
      ? normalized
          .split('')
          .map((character) => `${character}${character}`)
          .join('')
      : normalized;

  if (!/^[\da-f]{6}$/i.test(expanded)) {
    throw new Error(`Unsupported color "${value}". Use #RGB or #RRGGBB.`);
  }

  return {
    red: Number.parseInt(expanded.slice(0, 2), 16),
    green: Number.parseInt(expanded.slice(2, 4), 16),
    blue: Number.parseInt(expanded.slice(4, 6), 16),
  };
}

function relativeLuminance(color: RgbColor): number {
  const channels = [color.red, color.green, color.blue].map((channel) => {
    const normalized = channel / 255;
    return normalized <= 0.04045
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  });

  return (
    0.2126 * (channels[0] ?? 0) +
    0.7152 * (channels[1] ?? 0) +
    0.0722 * (channels[2] ?? 0)
  );
}

export function contrastRatio(foreground: string, background: string): number {
  const foregroundLuminance = relativeLuminance(parseHexColor(foreground));
  const backgroundLuminance = relativeLuminance(parseHexColor(background));
  const lighter = Math.max(foregroundLuminance, backgroundLuminance);
  const darker = Math.min(foregroundLuminance, backgroundLuminance);
  return (lighter + 0.05) / (darker + 0.05);
}

export interface ContrastPair {
  foreground: string;
  background: string;
  minimum: number;
}

export function parseCssColorTokens(
  css: string,
): Readonly<Record<string, string>> {
  const tokens: Record<string, string> = {};
  const colorToken =
    /(--[\w-]+)\s*:\s*(#(?:[\da-f]{6}|[\da-f]{3}))\s*;/gi;

  for (const match of css.matchAll(colorToken)) {
    const name = match[1];
    const value = match[2];
    if (name && value) {
      tokens[name] = value;
    }
  }

  return tokens;
}

export function parseCssThemeTokens(
  css: string,
): Readonly<Record<string, Readonly<Record<string, string>>>> {
  const themes: Record<string, Record<string, string>> = {};
  const shared: Record<string, string> = {};
  const block = /([^{}]+)\{([^{}]*)\}/g;

  for (const match of css.matchAll(block)) {
    const selector = match[1] ?? '';
    const body = match[2] ?? '';
    const tokens = parseCssColorTokens(body);
    const names = [...selector.matchAll(/\[data-theme="([\w-]+)"\]/g)].map(
      (theme) => theme[1] as string,
    );

    if (names.length === 0) {
      Object.assign(shared, tokens);
      continue;
    }

    for (const name of names) {
      themes[name] = { ...(themes[name] ?? {}), ...tokens };
    }
  }

  return Object.fromEntries(
    Object.entries(themes).map(([name, tokens]) => [
      name,
      { ...shared, ...tokens },
    ]),
  );
}

export function auditContrastPairs(
  tokens: Readonly<Record<string, string>>,
  pairs: readonly ContrastPair[],
): AccessibilityFinding[] {
  return pairs.flatMap((pair) => {
    const foreground = tokens[pair.foreground];
    const background = tokens[pair.background];
    if (!foreground || !background) {
      return [
        {
          rule: 'contrast-token-missing',
          impact: 'serious' as const,
          message: `Missing contrast token pair ${pair.foreground}/${pair.background}.`,
        },
      ];
    }

    const ratio = contrastRatio(foreground, background);
    return ratio < pair.minimum
      ? [
          {
            rule: 'color-contrast',
            impact: 'serious' as const,
            message: `${pair.foreground}/${pair.background} has contrast ${ratio.toFixed(2)}:1; expected at least ${pair.minimum}:1.`,
          },
        ]
      : [];
  });
}

export interface TargetDimensions {
  width: number;
  height: number;
}

export interface TargetSizeOptions {
  minimum?: number;
}

export function auditTargetSize(
  dimensions: TargetDimensions,
  { minimum = 24 }: TargetSizeOptions = {},
): AccessibilityFinding[] {
  if (dimensions.width >= minimum && dimensions.height >= minimum) {
    return [];
  }

  return [
    {
      rule: 'target-size',
      impact: 'serious',
      message: `Target is ${dimensions.width}×${dimensions.height} CSS pixels; expected at least ${minimum}×${minimum}.`,
    },
  ];
}

export interface HorizontalReflowDimensions {
  viewportWidth: number;
  contentWidth: number;
  tolerance?: number;
}

export function cssViewportWidthAtZoom(
  deviceViewportWidth: number,
  zoomPercent: number,
): number {
  if (zoomPercent <= 0) {
    throw new Error('Zoom percentage must be greater than zero.');
  }

  return deviceViewportWidth / (zoomPercent / 100);
}

export function auditHorizontalReflow({
  viewportWidth,
  contentWidth,
  tolerance = 1,
}: HorizontalReflowDimensions): AccessibilityFinding[] {
  if (contentWidth <= viewportWidth + tolerance) {
    return [];
  }

  return [
    {
      rule: 'horizontal-reflow',
      impact: 'serious',
      message: `Content width ${contentWidth}px exceeds the ${viewportWidth}px viewport.`,
    },
  ];
}

function authorProvidedName(element: Element): string {
  const ariaLabel = element.getAttribute('aria-label')?.trim();
  if (ariaLabel) {
    return ariaLabel;
  }

  const labelledBy = element.getAttribute('aria-labelledby')?.trim();
  if (labelledBy) {
    return labelledBy
      .split(/\s+/)
      .map((id) => element.ownerDocument.getElementById(id)?.textContent ?? '')
      .join(' ')
      .trim();
  }

  return element.getAttribute('title')?.trim() ?? '';
}

function controlName(element: Element): string {
  const authorName = authorProvidedName(element);
  if (authorName) {
    return authorName;
  }

  if (element instanceof HTMLInputElement) {
    const labels = Array.from(element.labels ?? []);
    const label = labels.map((item) => item.textContent ?? '').join(' ').trim();
    if (label) {
      return label;
    }
    if (element.type === 'submit' || element.type === 'button') {
      return element.value.trim();
    }
  }

  if (element instanceof HTMLImageElement) {
    return element.alt.trim();
  }

  return element.textContent?.trim() ?? '';
}

function elementsWithin(root: ParentNode, selector: string): Element[] {
  return Array.from(root.querySelectorAll(selector));
}

export function auditAccessibilityTree(
  root: ParentNode,
): AccessibilityFinding[] {
  const findings: AccessibilityFinding[] = [];

  const ids = new Map<string, Element>();
  for (const element of elementsWithin(root, '[id]')) {
    const id = element.id;
    const duplicate = ids.get(id);
    if (id && duplicate) {
      findings.push({
        rule: 'duplicate-id',
        impact: 'critical',
        message: `Duplicate id "${id}" prevents reliable accessibility references.`,
        element,
      });
    } else if (id) {
      ids.set(id, element);
    }
  }

  for (const element of elementsWithin(
    root,
    '[aria-labelledby], [aria-describedby]',
  )) {
    for (const attribute of ['aria-labelledby', 'aria-describedby']) {
      const references = element.getAttribute(attribute)?.trim().split(/\s+/);
      if (
        references?.some(
          (reference) => !element.ownerDocument.getElementById(reference),
        )
      ) {
        findings.push({
          rule: 'aria-reference-valid',
          impact: 'serious',
          message: `${attribute} must reference existing ids.`,
          element,
        });
      }
    }
  }

  for (const dialog of elementsWithin(root, '[role="dialog"], dialog')) {
    if (!authorProvidedName(dialog)) {
      findings.push({
        rule: 'dialog-name',
        impact: 'serious',
        message: 'Dialogs require an author-provided accessible name.',
        element: dialog,
      });
    }
  }

  for (const grid of elementsWithin(root, '[role="grid"]')) {
    const cells = elementsWithin(grid, '[role="gridcell"]');
    const tabbableCells = cells.filter(
      (cell) => cell instanceof HTMLElement && cell.tabIndex === 0,
    );
    if (cells.length === 0) {
      findings.push({
        rule: 'grid-has-cells',
        impact: 'serious',
        message: 'ARIA grids require gridcell descendants.',
        element: grid,
      });
    } else if (tabbableCells.length !== 1) {
      findings.push({
        rule: 'grid-single-tab-stop',
        impact: 'serious',
        message: 'ARIA grids require exactly one roving tab stop.',
        element: grid,
      });
    }
  }

  for (const control of elementsWithin(
    root,
    'button, a[href], input:not([type="hidden"]), select, textarea, [role="button"]',
  )) {
    if (!controlName(control)) {
      findings.push({
        rule: 'control-name',
        impact: 'critical',
        message: 'Interactive controls require an accessible name.',
        element: control,
      });
    }
  }

  return findings;
}
