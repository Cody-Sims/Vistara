import { render, screen } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  auditAccessibilityTree,
  auditContrastPairs,
  auditHorizontalReflow,
  auditTargetSize,
  cssViewportWidthAtZoom,
  contrastRatio,
  parseCssColorTokens,
} from './audit';
import { VisuallyHidden } from './VisuallyHidden';

const themeTokens = readFileSync(
  resolve(process.cwd(), 'src/styles/tokens.css'),
  'utf8',
);

describe('accessibility audit foundations', () => {
  it('calculates WCAG contrast and audits token pairs', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBe(21);

    const tokens = parseCssColorTokens(themeTokens);
    const findings = auditContrastPairs(
      tokens,
      [
        {
          foreground: '--color-text',
          background: '--color-canvas',
          minimum: 4.5,
        },
        {
          foreground: '--color-muted',
          background: '--color-canvas',
          minimum: 4.5,
        },
        {
          foreground: '--color-on-accent',
          background: '--color-accent',
          minimum: 4.5,
        },
      ],
    );

    expect(findings).toEqual([]);
  });

  it('checks minimum and preferred touch target dimensions', () => {
    expect(auditTargetSize({ width: 24, height: 24 })).toEqual([]);
    expect(
      auditTargetSize({ width: 40, height: 44 }, { minimum: 44 }),
    ).toEqual([
      expect.objectContaining({
        rule: 'target-size',
        impact: 'serious',
      }),
    ]);
  });

  it('models the 320 CSS pixel viewport at 400 percent zoom', () => {
    const cssViewportWidth = cssViewportWidthAtZoom(1280, 400);
    expect(cssViewportWidth).toBe(320);
    expect(
      auditHorizontalReflow({
        viewportWidth: cssViewportWidth,
        contentWidth: 320,
      }),
    ).toEqual([]);
    expect(
      auditHorizontalReflow({
        viewportWidth: cssViewportWidth,
        contentWidth: 480,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'horizontal-reflow',
        impact: 'serious',
      }),
    ]);
  });

  it('reports serious semantic failures in an owned test harness', () => {
    const { container } = render(
      <>
        <div role="dialog" aria-modal="true">
          Dialog body
        </div>
        <div role="grid">
          <div role="row">
            <button role="gridcell" tabIndex={0}>
              One
            </button>
            <button role="gridcell" tabIndex={0}>
              Two
            </button>
          </div>
        </div>
      </>,
    );

    expect(auditAccessibilityTree(container)).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          rule: 'dialog-name',
          impact: 'serious',
        }),
        expect.objectContaining({
          rule: 'grid-single-tab-stop',
          impact: 'serious',
        }),
      ]),
    );
  });

  it('has zero serious or critical findings for the owned baseline', () => {
    const { container } = render(
      <>
        <div role="dialog" aria-modal="true" aria-label="Details">
          <button>Close</button>
        </div>
        <div
          role="grid"
          aria-label="Media"
          aria-rowcount={1}
          aria-colcount={2}
        >
          <div role="row" aria-rowindex={1}>
            <button role="gridcell" aria-colindex={1} tabIndex={0}>
              One
            </button>
            <button role="gridcell" aria-colindex={2} tabIndex={-1}>
              Two
            </button>
          </div>
        </div>
        <div role="status" aria-live="polite" aria-atomic="true">
          Ready
        </div>
        <VisuallyHidden>Additional context</VisuallyHidden>
      </>,
    );

    const seriousFindings = auditAccessibilityTree(container).filter(
      ({ impact }) => impact === 'serious' || impact === 'critical',
    );
    expect(seriousFindings).toEqual([]);
    expect(screen.getByText('Additional context')).toBeInTheDocument();
  });
});
