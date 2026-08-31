/** Types for the dependency-free brand artwork generator. */
export type BrandColour = readonly [number, number, number];
export declare const palette: Readonly<
  Record<
    | 'ink'
    | 'inkSoft'
    | 'parchment'
    | 'accent'
    | 'accentDeep'
    | 'accentDark'
    | 'sun',
    BrandColour
  >
>;
export declare const brandSvg: string;
export declare function deflate(data: Uint8Array): Buffer;
export declare function zlibCompress(data: Uint8Array): Buffer;
export declare function filterScanlines(
  width: number,
  height: number,
  rgba: Uint8Array,
): Buffer;
export declare function encodePng(
  width: number,
  height: number,
  rgba: Uint8Array,
): Buffer;
export declare function encodeIco(
  images: { size: number; rgba: Uint8Array }[],
): Buffer;
export declare function buildBrandAssets(): Map<string, Buffer>;
