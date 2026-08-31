/* global Buffer */

/**
 * Original Vistara artwork rendered without any drawing dependency. Every mark
 * is described as geometry here and rasterised into PNG bytes, so the
 * committed assets can always be regenerated and verified from source.
 *
 * The DEFLATE stream is produced by the encoder below rather than by
 * `node:zlib`, because the compressor Node links against differs between
 * platforms and releases: the same pixels compress to different (equally
 * valid) bytes on a macOS build linked against zlib 1.2.x and on a Linux
 * build carrying Chromium's bundled zlib. Owning the compressor keeps the
 * committed assets byte-identical everywhere.
 */

export const palette = {
  ink: [17, 17, 15],
  inkSoft: [30, 32, 27],
  parchment: [242, 240, 233],
  accent: [121, 201, 169],
  accentDeep: [47, 128, 103],
  accentDark: [25, 56, 46],
  sun: [239, 189, 103],
};

const MIN_MATCH = 3;
const MAX_MATCH = 258;
const WINDOW_SIZE = 32_768;
const HASH_BITS = 15;
const HASH_SIZE = 1 << HASH_BITS;
const HASH_MASK = HASH_SIZE - 1;
const MAX_CHAIN = 4096;
const NICE_MATCH = MAX_MATCH;
const END_OF_BLOCK = 256;
const LITERAL_SYMBOLS = 286;
const DISTANCE_SYMBOLS = 30;
const MAX_CODE_BITS = 15;
const MAX_CODE_LENGTH_BITS = 7;

const LENGTH_BASE = [
  3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67,
  83, 99, 115, 131, 163, 195, 227, 258,
];
const LENGTH_EXTRA_BITS = [
  0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5,
  5, 5, 0,
];
const DISTANCE_BASE = [
  1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
  1025, 1537, 2049, 3073, 4097, 6145, 8193, 12_289, 16_385, 24_577,
];
const DISTANCE_EXTRA_BITS = [
  0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11,
  11, 12, 12, 13, 13,
];
const CODE_LENGTH_ORDER = [
  16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
];

const FIXED_LITERAL_LENGTHS = (() => {
  const lengths = new Uint8Array(288);
  lengths.fill(8, 0, 144);
  lengths.fill(9, 144, 256);
  lengths.fill(7, 256, 280);
  lengths.fill(8, 280, 288);
  return lengths;
})();
const FIXED_DISTANCE_LENGTHS = new Uint8Array(30).fill(5);

/** Maps a match length to its DEFLATE length symbol index. */
const lengthSymbolOf = (() => {
  const table = new Uint8Array(MAX_MATCH + 1);
  for (let index = 0; index < LENGTH_BASE.length; index += 1) {
    const last =
      index + 1 < LENGTH_BASE.length ? LENGTH_BASE[index + 1] - 1 : MAX_MATCH;
    table.fill(index, LENGTH_BASE[index], last + 1);
  }
  return table;
})();

function distanceSymbolOf(distance) {
  let symbol = DISTANCE_SYMBOLS - 1;
  while (symbol > 0 && DISTANCE_BASE[symbol] > distance) {
    symbol -= 1;
  }
  return symbol;
}

function createBitWriter() {
  let bytes = new Uint8Array(4096);
  let length = 0;
  let accumulator = 0;
  let pending = 0;

  function push(byte) {
    if (length === bytes.length) {
      const grown = new Uint8Array(bytes.length * 2);
      grown.set(bytes);
      bytes = grown;
    }
    bytes[length] = byte;
    length += 1;
  }

  return {
    writeBits(value, count) {
      accumulator |= value << pending;
      pending += count;
      while (pending >= 8) {
        push(accumulator & 0xff);
        accumulator >>>= 8;
        pending -= 8;
      }
    },
    finish() {
      if (pending > 0) {
        push(accumulator & 0xff);
        accumulator = 0;
        pending = 0;
      }
      return Buffer.from(bytes.subarray(0, length));
    },
  };
}

function reverseBits(value, count) {
  let reversed = 0;
  for (let index = 0; index < count; index += 1) {
    reversed = (reversed << 1) | ((value >>> index) & 1);
  }
  return reversed >>> 0;
}

function mergeByWeight(left, right) {
  const merged = [];
  let leftIndex = 0;
  let rightIndex = 0;
  while (leftIndex < left.length && rightIndex < right.length) {
    if (left[leftIndex].weight <= right[rightIndex].weight) {
      merged.push(left[leftIndex]);
      leftIndex += 1;
    } else {
      merged.push(right[rightIndex]);
      rightIndex += 1;
    }
  }
  while (leftIndex < left.length) {
    merged.push(left[leftIndex]);
    leftIndex += 1;
  }
  while (rightIndex < right.length) {
    merged.push(right[rightIndex]);
    rightIndex += 1;
  }
  return merged;
}

/**
 * Length-limited Huffman code lengths via package-merge. The algorithm is
 * integer-only and its tie-breaks are fully ordered, so identical frequencies
 * always yield identical code lengths on every platform.
 */
function codeLengthsFor(frequencies, limit) {
  const lengths = new Uint8Array(frequencies.length);
  const leaves = [];
  for (let symbol = 0; symbol < frequencies.length; symbol += 1) {
    if (frequencies[symbol] > 0) {
      leaves.push({ weight: frequencies[symbol], symbols: [symbol] });
    }
  }

  if (leaves.length === 0) {
    return lengths;
  }
  if (leaves.length === 1) {
    lengths[leaves[0].symbols[0]] = 1;
    return lengths;
  }

  leaves.sort((a, b) => a.weight - b.weight || a.symbols[0] - b.symbols[0]);

  let level = leaves;
  for (let round = 1; round < limit; round += 1) {
    const packaged = [];
    for (let index = 0; index + 1 < level.length; index += 2) {
      packaged.push({
        weight: level[index].weight + level[index + 1].weight,
        symbols: [...level[index].symbols, ...level[index + 1].symbols],
      });
    }
    level = mergeByWeight(leaves, packaged);
  }

  for (let index = 0; index < leaves.length * 2 - 2; index += 1) {
    for (const symbol of level[index].symbols) {
      lengths[symbol] += 1;
    }
  }
  return lengths;
}

/** Canonical DEFLATE codes, pre-reversed for least-significant-bit output. */
function canonicalCodes(lengths) {
  const codes = new Uint16Array(lengths.length);
  let longest = 0;
  for (const length of lengths) {
    if (length > longest) {
      longest = length;
    }
  }
  if (longest === 0) {
    return codes;
  }

  const counts = new Uint32Array(longest + 1);
  for (const length of lengths) {
    if (length > 0) {
      counts[length] += 1;
    }
  }

  const nextCode = new Uint32Array(longest + 1);
  let code = 0;
  for (let length = 1; length <= longest; length += 1) {
    code = (code + counts[length - 1]) << 1;
    nextCode[length] = code;
  }

  for (let symbol = 0; symbol < lengths.length; symbol += 1) {
    const length = lengths[symbol];
    if (length > 0) {
      codes[symbol] = reverseBits(nextCode[length], length);
      nextCode[length] += 1;
    }
  }
  return codes;
}

/**
 * Greedy matching with a single-step lazy lookahead over bounded hash chains.
 * The chain and lookahead bounds are constants so the token stream depends
 * only on the input bytes.
 */
function tokenize(data) {
  const head = new Int32Array(HASH_SIZE).fill(-1);
  const chain = new Int32Array(Math.max(data.length, 1)).fill(-1);
  const tokenLengths = new Uint16Array(data.length + 1);
  const tokenDistances = new Uint16Array(data.length + 1);
  const tokenLiterals = new Uint16Array(data.length + 1);
  let tokenCount = 0;

  const hashAt = (position) =>
    ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) &
    HASH_MASK;

  function insert(position) {
    if (position + MIN_MATCH > data.length) {
      return;
    }
    const key = hashAt(position);
    chain[position] = head[key];
    head[key] = position;
  }

  function insertRange(from, to) {
    for (let position = from; position < to; position += 1) {
      insert(position);
    }
  }

  function searchAndInsert(position) {
    if (position + MIN_MATCH > data.length) {
      insert(position);
      return { length: 0, distance: 0 };
    }

    const oldest = position - WINDOW_SIZE;
    const ceiling = Math.min(MAX_MATCH, data.length - position);
    let candidate = head[hashAt(position)];
    let attempts = MAX_CHAIN;
    let bestLength = 0;
    let bestDistance = 0;

    while (candidate >= 0 && candidate > oldest && attempts > 0) {
      attempts -= 1;
      if (data[candidate + bestLength] === data[position + bestLength]) {
        let length = 0;
        while (
          length < ceiling &&
          data[candidate + length] === data[position + length]
        ) {
          length += 1;
        }
        if (length > bestLength) {
          bestLength = length;
          bestDistance = position - candidate;
          if (length >= NICE_MATCH || length === ceiling) {
            break;
          }
        }
      }
      candidate = chain[candidate];
    }

    insert(position);
    return bestLength >= MIN_MATCH
      ? { length: bestLength, distance: bestDistance }
      : { length: 0, distance: 0 };
  }

  function emitLiteral(byte) {
    tokenLengths[tokenCount] = 0;
    tokenLiterals[tokenCount] = byte;
    tokenCount += 1;
  }

  function emitMatch(match) {
    tokenLengths[tokenCount] = match.length;
    tokenDistances[tokenCount] = match.distance;
    tokenCount += 1;
  }

  let position = 0;
  let carried = null;
  while (position < data.length) {
    const current = carried ?? searchAndInsert(position);
    carried = null;

    if (current.length === 0) {
      emitLiteral(data[position]);
      position += 1;
      continue;
    }

    if (current.length < NICE_MATCH && position + 1 < data.length) {
      const next = searchAndInsert(position + 1);
      if (next.length > current.length) {
        emitLiteral(data[position]);
        position += 1;
        carried = next;
        continue;
      }
      emitMatch(current);
      insertRange(position + 2, position + current.length);
      position += current.length;
      continue;
    }

    emitMatch(current);
    insertRange(position + 1, position + current.length);
    position += current.length;
  }

  return { tokenLengths, tokenDistances, tokenLiterals, tokenCount };
}

function tallySymbols(tokens) {
  const literalFrequencies = new Uint32Array(LITERAL_SYMBOLS);
  const distanceFrequencies = new Uint32Array(DISTANCE_SYMBOLS);
  for (let index = 0; index < tokens.tokenCount; index += 1) {
    const length = tokens.tokenLengths[index];
    if (length === 0) {
      literalFrequencies[tokens.tokenLiterals[index]] += 1;
    } else {
      literalFrequencies[END_OF_BLOCK + 1 + lengthSymbolOf[length]] += 1;
      distanceFrequencies[distanceSymbolOf(tokens.tokenDistances[index])] += 1;
    }
  }
  literalFrequencies[END_OF_BLOCK] += 1;
  return { literalFrequencies, distanceFrequencies };
}

/** Guarantees a complete prefix code; a one-symbol tree is not decodable. */
function completeTree(lengths, minimumSymbols) {
  let used = 0;
  for (const length of lengths) {
    if (length > 0) {
      used += 1;
    }
  }
  for (let symbol = 0; used < minimumSymbols && symbol < lengths.length; symbol += 1) {
    if (lengths[symbol] === 0) {
      lengths[symbol] = 1;
      used += 1;
    }
  }
  return lengths;
}

/** Run-length encodes the concatenated literal and distance code lengths. */
function encodeCodeLengths(literalLengths, distanceLengths) {
  let literalCount = LITERAL_SYMBOLS;
  while (literalCount > 257 && literalLengths[literalCount - 1] === 0) {
    literalCount -= 1;
  }
  let distanceCount = DISTANCE_SYMBOLS;
  while (distanceCount > 1 && distanceLengths[distanceCount - 1] === 0) {
    distanceCount -= 1;
  }

  const combined = [
    ...literalLengths.subarray(0, literalCount),
    ...distanceLengths.subarray(0, distanceCount),
  ];

  const symbols = [];
  let index = 0;
  while (index < combined.length) {
    const value = combined[index];
    let run = 1;
    while (index + run < combined.length && combined[index + run] === value) {
      run += 1;
    }

    if (value === 0) {
      while (run >= 11) {
        const take = Math.min(run, 138);
        symbols.push({ symbol: 18, extraBits: 7, extraValue: take - 11 });
        run -= take;
        index += take;
      }
      while (run >= 3) {
        const take = Math.min(run, 10);
        symbols.push({ symbol: 17, extraBits: 3, extraValue: take - 3 });
        run -= take;
        index += take;
      }
      while (run > 0) {
        symbols.push({ symbol: 0, extraBits: 0, extraValue: 0 });
        run -= 1;
        index += 1;
      }
      continue;
    }

    symbols.push({ symbol: value, extraBits: 0, extraValue: 0 });
    run -= 1;
    index += 1;
    while (run >= 3) {
      const take = Math.min(run, 6);
      symbols.push({ symbol: 16, extraBits: 2, extraValue: take - 3 });
      run -= take;
      index += take;
    }
    while (run > 0) {
      symbols.push({ symbol: value, extraBits: 0, extraValue: 0 });
      run -= 1;
      index += 1;
    }
  }

  return { symbols, literalCount, distanceCount };
}

function payloadBits(tokens, literalLengths, distanceLengths) {
  let bits = 0;
  for (let index = 0; index < tokens.tokenCount; index += 1) {
    const length = tokens.tokenLengths[index];
    if (length === 0) {
      bits += literalLengths[tokens.tokenLiterals[index]];
      continue;
    }
    const lengthSymbol = lengthSymbolOf[length];
    const distanceSymbol = distanceSymbolOf(tokens.tokenDistances[index]);
    bits +=
      literalLengths[END_OF_BLOCK + 1 + lengthSymbol] +
      LENGTH_EXTRA_BITS[lengthSymbol] +
      distanceLengths[distanceSymbol] +
      DISTANCE_EXTRA_BITS[distanceSymbol];
  }
  return bits + literalLengths[END_OF_BLOCK];
}

function writePayload(writer, tokens, trees) {
  const { literalCodes, literalLengths, distanceCodes, distanceLengths } =
    trees;
  for (let index = 0; index < tokens.tokenCount; index += 1) {
    const length = tokens.tokenLengths[index];
    if (length === 0) {
      const literal = tokens.tokenLiterals[index];
      writer.writeBits(literalCodes[literal], literalLengths[literal]);
      continue;
    }

    const lengthSymbol = lengthSymbolOf[length];
    const literalSymbol = END_OF_BLOCK + 1 + lengthSymbol;
    writer.writeBits(literalCodes[literalSymbol], literalLengths[literalSymbol]);
    writer.writeBits(
      length - LENGTH_BASE[lengthSymbol],
      LENGTH_EXTRA_BITS[lengthSymbol],
    );

    const distance = tokens.tokenDistances[index];
    const distanceSymbol = distanceSymbolOf(distance);
    writer.writeBits(
      distanceCodes[distanceSymbol],
      distanceLengths[distanceSymbol],
    );
    writer.writeBits(
      distance - DISTANCE_BASE[distanceSymbol],
      DISTANCE_EXTRA_BITS[distanceSymbol],
    );
  }

  writer.writeBits(literalCodes[END_OF_BLOCK], literalLengths[END_OF_BLOCK]);
}

/**
 * Compresses to a single DEFLATE block, choosing the cheaper of the fixed and
 * dynamic Huffman encodings by exact bit cost.
 */
export function deflate(data) {
  const tokens = tokenize(data);
  const { literalFrequencies, distanceFrequencies } = tallySymbols(tokens);

  const literalLengths = completeTree(
    codeLengthsFor(literalFrequencies, MAX_CODE_BITS),
    2,
  );
  const distanceLengths = completeTree(
    codeLengthsFor(distanceFrequencies, MAX_CODE_BITS),
    2,
  );

  const { symbols, literalCount, distanceCount } = encodeCodeLengths(
    literalLengths,
    distanceLengths,
  );

  const codeLengthFrequencies = new Uint32Array(19);
  for (const entry of symbols) {
    codeLengthFrequencies[entry.symbol] += 1;
  }
  const codeLengthLengths = codeLengthsFor(
    codeLengthFrequencies,
    MAX_CODE_LENGTH_BITS,
  );
  const codeLengthCodes = canonicalCodes(codeLengthLengths);

  let orderCount = CODE_LENGTH_ORDER.length;
  while (
    orderCount > 4 &&
    codeLengthLengths[CODE_LENGTH_ORDER[orderCount - 1]] === 0
  ) {
    orderCount -= 1;
  }

  let headerBits = 3 + 5 + 5 + 4 + orderCount * 3;
  for (const entry of symbols) {
    headerBits += codeLengthLengths[entry.symbol] + entry.extraBits;
  }
  const dynamicBits =
    headerBits + payloadBits(tokens, literalLengths, distanceLengths);
  const fixedBits =
    3 + payloadBits(tokens, FIXED_LITERAL_LENGTHS, FIXED_DISTANCE_LENGTHS);

  const writer = createBitWriter();
  writer.writeBits(1, 1);

  if (fixedBits <= dynamicBits) {
    writer.writeBits(1, 2);
    writePayload(writer, tokens, {
      literalCodes: canonicalCodes(FIXED_LITERAL_LENGTHS),
      literalLengths: FIXED_LITERAL_LENGTHS,
      distanceCodes: canonicalCodes(FIXED_DISTANCE_LENGTHS),
      distanceLengths: FIXED_DISTANCE_LENGTHS,
    });
    return writer.finish();
  }

  writer.writeBits(2, 2);
  writer.writeBits(literalCount - 257, 5);
  writer.writeBits(distanceCount - 1, 5);
  writer.writeBits(orderCount - 4, 4);
  for (let index = 0; index < orderCount; index += 1) {
    writer.writeBits(codeLengthLengths[CODE_LENGTH_ORDER[index]], 3);
  }
  for (const entry of symbols) {
    writer.writeBits(
      codeLengthCodes[entry.symbol],
      codeLengthLengths[entry.symbol],
    );
    writer.writeBits(entry.extraValue, entry.extraBits);
  }

  writePayload(writer, tokens, {
    literalCodes: canonicalCodes(literalLengths),
    literalLengths,
    distanceCodes: canonicalCodes(distanceLengths),
    distanceLengths,
  });
  return writer.finish();
}

function adler32(bytes) {
  let low = 1;
  let high = 0;
  for (let index = 0; index < bytes.length; index += 1) {
    low = (low + bytes[index]) % 65_521;
    high = (high + low) % 65_521;
  }
  return ((high << 16) | low) >>> 0;
}

/** RFC 1950 container around the deterministic DEFLATE stream above. */
export function zlibCompress(data) {
  const checksum = Buffer.alloc(4);
  checksum.writeUInt32BE(adler32(data), 0);
  return Buffer.concat([Buffer.from([0x78, 0xda]), deflate(data), checksum]);
}

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let index = 0; index < 256; index += 1) {
    let value = index;
    for (let bit = 0; bit < 8; bit += 1) {
      value = value & 1 ? 0xed_b8_83_20 ^ (value >>> 1) : value >>> 1;
    }
    table[index] = value >>> 0;
  }
  return table;
})();

function crc32(bytes) {
  let crc = 0xff_ff_ff_ff;
  for (const byte of bytes) {
    crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xff_ff_ff_ff) >>> 0;
}

function chunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii');
  const body = Buffer.concat([typeBytes, Buffer.from(data)]);
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([length, body, crc]);
}

const BYTES_PER_PIXEL = 4;

function paethPredictor(left, above, upperLeft) {
  const estimate = left + above - upperLeft;
  const distanceLeft = Math.abs(estimate - left);
  const distanceAbove = Math.abs(estimate - above);
  const distanceUpperLeft = Math.abs(estimate - upperLeft);
  if (distanceLeft <= distanceAbove && distanceLeft <= distanceUpperLeft) {
    return left;
  }
  return distanceAbove <= distanceUpperLeft ? above : upperLeft;
}

function applyFilter(type, row, previous, stride, output) {
  for (let index = 0; index < stride; index += 1) {
    const raw = row[index];
    const left = index >= BYTES_PER_PIXEL ? row[index - BYTES_PER_PIXEL] : 0;
    const above = previous[index];
    const upperLeft =
      index >= BYTES_PER_PIXEL ? previous[index - BYTES_PER_PIXEL] : 0;
    let predictor = 0;
    if (type === 1) {
      predictor = left;
    } else if (type === 2) {
      predictor = above;
    } else if (type === 3) {
      predictor = (left + above) >> 1;
    } else if (type === 4) {
      predictor = paethPredictor(left, above, upperLeft);
    }
    output[index] = (raw - predictor) & 0xff;
  }
}

/** libpng's minimum-sum-of-absolute-differences heuristic, integer only. */
function filterCost(bytes, stride) {
  let cost = 0;
  for (let index = 0; index < stride; index += 1) {
    const value = bytes[index];
    cost += value < 128 ? value : 256 - value;
  }
  return cost;
}

/** Filter strategies compared by real compressed size, cheapest wins. */
const FILTER_STRATEGIES = [0, 2, 4, -1];

/**
 * Applies one filter type to every scanline, or the adaptive
 * minimum-sum-of-absolute-differences heuristic when `strategy` is negative.
 */
export function filterScanlines(width, height, rgba, strategy = -1) {
  const stride = width * BYTES_PER_PIXEL;
  const raw = Buffer.alloc((stride + 1) * height);
  const candidate = Buffer.alloc(stride);
  let previous = Buffer.alloc(stride);

  for (let y = 0; y < height; y += 1) {
    const row = rgba.subarray(y * stride, (y + 1) * stride);
    const target = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));

    if (strategy >= 0) {
      applyFilter(strategy, row, previous, stride, target);
      raw[y * (stride + 1)] = strategy;
    } else {
      let bestType = 0;
      let bestCost = Number.POSITIVE_INFINITY;
      for (let type = 0; type <= 4; type += 1) {
        applyFilter(type, row, previous, stride, candidate);
        const cost = filterCost(candidate, stride);
        if (cost < bestCost) {
          bestCost = cost;
          bestType = type;
          candidate.copy(target);
        }
      }
      raw[y * (stride + 1)] = bestType;
    }

    previous = Buffer.from(row);
  }

  return raw;
}

export function encodePng(width, height, rgba) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 6;

  let smallest = null;
  for (const strategy of FILTER_STRATEGIES) {
    const compressed = zlibCompress(
      filterScanlines(width, height, rgba, strategy),
    );
    if (smallest === null || compressed.length < smallest.length) {
      smallest = compressed;
    }
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', smallest),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/**
 * Classic icon directory holding uncompressed 32-bit BGRA device-independent
 * bitmaps. Every consumer of `/favicon.ico`, including the pre-PNG Windows
 * shell readers, understands this form, and because nothing in it is
 * compressed the bytes cannot drift between platforms.
 */
export function encodeIco(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);

  const bitmaps = images.map(({ size, rgba }) => encodeIconBitmap(size, rgba));
  const directory = Buffer.alloc(16 * images.length);
  let offset = header.length + directory.length;

  for (let index = 0; index < images.length; index += 1) {
    const { size } = images[index];
    const entry = 16 * index;
    directory[entry] = size >= 256 ? 0 : size;
    directory[entry + 1] = size >= 256 ? 0 : size;
    directory[entry + 2] = 0;
    directory[entry + 3] = 0;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(bitmaps[index].length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += bitmaps[index].length;
  }

  return Buffer.concat([header, directory, ...bitmaps]);
}

function encodeIconBitmap(size, rgba) {
  const colorStride = size * 4;
  const maskStride = Math.ceil(size / 32) * 4;
  const color = Buffer.alloc(colorStride * size);
  const mask = Buffer.alloc(maskStride * size);

  // Icon bitmaps are stored bottom-up, as blue-green-red-alpha, with a
  // one-bit transparency mask that legacy readers fall back to.
  for (let y = 0; y < size; y += 1) {
    const source = (size - 1 - y) * colorStride;
    for (let x = 0; x < size; x += 1) {
      const pixel = source + x * 4;
      const target = y * colorStride + x * 4;
      color[target] = rgba[pixel + 2];
      color[target + 1] = rgba[pixel + 1];
      color[target + 2] = rgba[pixel];
      color[target + 3] = rgba[pixel + 3];
      if (rgba[pixel + 3] === 0) {
        mask[y * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
      }
    }
  }

  const info = Buffer.alloc(40);
  info.writeUInt32LE(40, 0);
  info.writeInt32LE(size, 4);
  info.writeInt32LE(size * 2, 8);
  info.writeUInt16LE(1, 12);
  info.writeUInt16LE(32, 14);
  info.writeUInt32LE(0, 16);
  info.writeUInt32LE(color.length + mask.length, 20);

  return Buffer.concat([info, color, mask]);
}

function clamp01(value) {
  return value < 0 ? 0 : value > 1 ? 1 : value;
}

function mix(from, to, amount) {
  const t = clamp01(amount);
  return [
    from[0] + (to[0] - from[0]) * t,
    from[1] + (to[1] - from[1]) * t,
    from[2] + (to[2] - from[2]) * t,
  ];
}

function over(base, color, alpha) {
  const a = clamp01(alpha);
  return [
    base[0] + (color[0] - base[0]) * a,
    base[1] + (color[1] - base[1]) * a,
    base[2] + (color[2] - base[2]) * a,
    Math.max(base[3], a),
  ];
}

function insideRoundedRect(x, y, rect) {
  const { left, top, width, height, radius } = rect;
  const right = left + width;
  const bottom = top + height;
  if (x < left || x > right || y < top || y > bottom) {
    return false;
  }

  const cx = Math.min(Math.max(x, left + radius), right - radius);
  const cy = Math.min(Math.max(y, top + radius), bottom - radius);
  return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
}

function insideCircle(x, y, cx, cy, radius) {
  return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
}

function insideTriangle(x, y, [ax, ay], [bx, by], [cx, cy]) {
  const area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
  const s = ((bx - ax) * (y - ay) - (x - ax) * (by - ay)) / area;
  const t = ((x - ax) * (cy - ay) - (cx - ax) * (y - ay)) / area;
  return s >= 0 && t >= 0 && s + t <= 1;
}

function render(width, height, shade, samples = 3) {
  const rgba = new Uint8Array(width * height * 4);

  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      let red = 0;
      let green = 0;
      let blue = 0;
      let alpha = 0;

      for (let sy = 0; sy < samples; sy += 1) {
        for (let sx = 0; sx < samples; sx += 1) {
          const px = x + (sx + 0.5) / samples;
          const py = y + (sy + 0.5) / samples;
          const [r, g, b, a] = shade(px, py);
          red += r * a;
          green += g * a;
          blue += b * a;
          alpha += a;
        }
      }

      const total = samples * samples;
      const offset = (y * width + x) * 4;
      const coverage = alpha / total;
      rgba[offset] = coverage === 0 ? 0 : Math.round(red / alpha);
      rgba[offset + 1] = coverage === 0 ? 0 : Math.round(green / alpha);
      rgba[offset + 2] = coverage === 0 ? 0 : Math.round(blue / alpha);
      rgba[offset + 3] = Math.round(coverage * 255);
    }
  }

  return rgba;
}

/**
 * The Vistara mark: a framed vista with a low sun and two ridges, drawn on a
 * square tile. `inset` reserves the maskable safe area.
 */
function markShader(size, { inset = 0, cornerRatio = 0.22 } = {}) {
  const tile = {
    left: inset,
    top: inset,
    width: size - inset * 2,
    height: size - inset * 2,
    radius: (size - inset * 2) * cornerRatio,
  };
  const u = (value) => tile.left + tile.width * value;
  const v = (value) => tile.top + tile.height * value;

  const sun = { x: u(0.7), y: v(0.3), r: tile.width * 0.12 };
  const horizon = v(0.78);
  const ridgeBack = [
    [u(0.02), horizon],
    [u(0.4), v(0.26)],
    [u(0.78), horizon],
  ];
  const ridgeFront = [
    [u(0.3), horizon],
    [u(0.64), v(0.44)],
    [u(0.98), horizon],
  ];

  return (x, y) => {
    if (!insideRoundedRect(x, y, tile)) {
      return [0, 0, 0, 0];
    }

    const depth = (y - tile.top) / tile.height;
    let pixel = [...mix(palette.ink, palette.inkSoft, depth * 0.9), 1];

    if (insideCircle(x, y, sun.x, sun.y, sun.r)) {
      pixel = over(pixel, palette.sun, 1);
    }

    if (insideTriangle(x, y, ...ridgeBack)) {
      pixel = over(pixel, palette.accentDeep, 1);
    }

    if (insideTriangle(x, y, ...ridgeFront)) {
      pixel = over(pixel, palette.accent, 1);
    }

    if (y >= horizon) {
      const below = (y - horizon) / (tile.top + tile.height - horizon);
      pixel = over(pixel, mix(palette.accentDark, palette.ink, below * 0.6), 1);
    }

    return pixel;
  };
}

function socialShader(width, height) {
  const sun = { x: width * 0.78, y: height * 0.26, r: height * 0.11 };
  const horizon = height * 0.74;
  const ridges = [
    {
      points: [
        [width * 0.24, horizon],
        [width * 0.56, height * 0.34],
        [width * 0.88, horizon],
      ],
      color: palette.accentDeep,
    },
    {
      points: [
        [width * 0.52, horizon],
        [width * 0.84, height * 0.48],
        [width * 1.16, horizon],
      ],
      color: palette.accent,
    },
  ];
  const tileSize = Math.round(height * 0.3);
  const tile = {
    left: Math.round(width * 0.07),
    top: Math.round(height * 0.5 - tileSize / 2),
    size: tileSize,
  };
  const tileShader = markShader(tileSize);

  return (x, y) => {
    let pixel = [...mix(palette.ink, palette.inkSoft, (y / height) * 1.1), 1];

    if (insideCircle(x, y, sun.x, sun.y, sun.r)) {
      pixel = over(pixel, palette.sun, 1);
    }

    for (const ridge of ridges) {
      if (insideTriangle(x, y, ...ridge.points)) {
        pixel = over(pixel, ridge.color, 1);
      }
    }

    if (y >= horizon) {
      const below = (y - horizon) / (height - horizon);
      pixel = over(pixel, mix(palette.accentDark, palette.ink, below * 0.7), 1);
    }

    const tx = x - tile.left;
    const ty = y - tile.top;
    if (tx >= 0 && ty >= 0 && tx < tile.size && ty < tile.size) {
      const [r, g, b, a] = tileShader(tx, ty);
      if (a > 0) {
        pixel = over(pixel, [r, g, b], a);
      }
    }

    return pixel;
  };
}

function markPng(size, options) {
  return encodePng(size, size, render(size, size, markShader(size, options)));
}

export const brandSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="Vistara">
  <title>Vistara</title>
  <defs>
    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#11110f" />
      <stop offset="1" stop-color="#1e201b" />
    </linearGradient>
    <clipPath id="frame">
      <rect x="0" y="0" width="64" height="64" rx="14" />
    </clipPath>
  </defs>
  <g clip-path="url(#frame)">
    <rect width="64" height="64" fill="url(#sky)" />
    <circle cx="44.8" cy="19.2" r="7.7" fill="#efbd67" />
    <path d="M1.3 49.9 25.6 16.6 49.9 49.9Z" fill="#2f8067" />
    <path d="M19.2 49.9 41 28.2 62.7 49.9Z" fill="#79c9a9" />
    <rect x="0" y="49.9" width="64" height="14.1" fill="#19382e" />
  </g>
</svg>
`;

export function buildBrandAssets() {
  const icon192 = markPng(192);
  const icon512 = markPng(512);
  const maskable = markPng(512, { inset: 512 * 0.12, cornerRatio: 0.5 });
  const appleTouch = markPng(180, { cornerRatio: 0 });
  const faviconPixels = render(32, 32, markShader(32, { cornerRatio: 0.22 }));
  const social = encodePng(
    1200,
    630,
    render(1200, 630, socialShader(1200, 630), 2),
  );

  // Every reference is relative to the manifest so the same file is valid at
  // the root and under a sub-path such as the GitHub Pages preview.
  const manifest = {
    name: 'Vistara',
    short_name: 'Vistara',
    description:
      'Vistara, a self-hosted image control plane and responsive gallery.',
    start_url: './',
    scope: './',
    display: 'standalone',
    orientation: 'any',
    background_color: '#11110f',
    theme_color: '#11110f',
    icons: [
      { src: './favicon.svg', sizes: 'any', type: 'image/svg+xml' },
      { src: './icon-192.png', sizes: '192x192', type: 'image/png' },
      { src: './icon-512.png', sizes: '512x512', type: 'image/png' },
      {
        src: './icon-maskable-512.png',
        sizes: '512x512',
        type: 'image/png',
        purpose: 'maskable',
      },
    ],
  };

  return new Map([
    ['public/favicon.svg', Buffer.from(brandSvg, 'utf8')],
    ['public/favicon.ico', encodeIco([{ size: 32, rgba: faviconPixels }])],
    ['public/icon-192.png', icon192],
    ['public/icon-512.png', icon512],
    ['public/icon-maskable-512.png', maskable],
    ['public/apple-touch-icon.png', appleTouch],
    ['public/social-preview.png', social],
    [
      'public/manifest.webmanifest',
      Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`, 'utf8'),
    ],
  ]);
}
