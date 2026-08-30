/** Types for the dependency-free brand artwork generator. */
export declare const palette: Readonly<Record<string, readonly number[]>>;
export declare const brandSvg: string;
export declare function encodePng(
  width: number,
  height: number,
  rgba: Uint8Array,
): Buffer;
export declare function encodeIco(png: Buffer, size: number): Buffer;
export declare function buildBrandAssets(): Map<string, Buffer>;
