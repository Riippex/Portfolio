// Strict IP canonicalization shared by the allowlist, the platform visitor and the signed
// identity. The backend implements the same algorithm (IpCanonicalizer.cs) and both are
// checked against docs/contracts/visitor-identity-v2.json, so a value that is canonical here
// is canonical there.
//
// Only exact addresses are accepted: no brackets, ports, zone ids, CIDR suffixes, wildcards,
// whitespace, leading zeros or non-decimal IPv4 forms. Anything else returns null.

const IPV4_PATTERN = /^(0|[1-9]\d{0,2})(\.(0|[1-9]\d{0,2})){3}$/;
const IPV6_GROUP_PATTERN = /^[0-9a-fA-F]{1,4}$/;
const MAX_IP_LENGTH = 45;

function parseIPv4Octets(value: string): number[] | null {
  if (!IPV4_PATTERN.test(value)) return null;
  const octets = value.split(".").map(Number);
  return octets.every((octet) => octet <= 255) ? octets : null;
}

function parseIPv6Groups(value: string): number[] | null {
  if (!/^[0-9a-fA-F:.]+$/.test(value) || !value.includes(":")) return null;

  const halves = value.split("::");
  if (halves.length > 2) return null;
  const compressed = halves.length === 2;

  const expand = (part: string, allowIPv4Tail: boolean): number[] | null => {
    if (part === "") return [];
    const tokens = part.split(":");
    const groups: number[] = [];
    for (let index = 0; index < tokens.length; index++) {
      const token = tokens[index];
      if (token.includes(".")) {
        if (!allowIPv4Tail || index !== tokens.length - 1) return null;
        const octets = parseIPv4Octets(token);
        if (!octets) return null;
        groups.push((octets[0] << 8) | octets[1], (octets[2] << 8) | octets[3]);
      } else {
        if (!IPV6_GROUP_PATTERN.test(token)) return null;
        groups.push(parseInt(token, 16));
      }
    }
    return groups;
  };

  const head = expand(halves[0], !compressed);
  const tail = compressed ? expand(halves[1], true) : [];
  if (!head || !tail) return null;

  if (!compressed) return head.length === 8 ? head : null;
  if (head.length + tail.length > 7) return null;
  return [...head, ...new Array<number>(8 - head.length - tail.length).fill(0), ...tail];
}

function formatIPv6(groups: number[]): string {
  let bestStart = -1;
  let bestLength = 0;
  for (let index = 0; index < 8; ) {
    if (groups[index] !== 0) {
      index++;
      continue;
    }
    let end = index;
    while (end < 8 && groups[end] === 0) end++;
    if (end - index > bestLength) {
      bestStart = index;
      bestLength = end - index;
    }
    index = end;
  }

  const hex = (group: number) => group.toString(16);
  if (bestLength < 2) return groups.map(hex).join(":");
  const before = groups.slice(0, bestStart).map(hex).join(":");
  const after = groups.slice(bestStart + bestLength).map(hex).join(":");
  return `${before}::${after}`;
}

/** Returns the canonical text of an exact IPv4 or IPv6 address, or null if it is not one. */
export function canonicalizeIp(value: unknown): string | null {
  if (typeof value !== "string" || value.length === 0 || value.length > MAX_IP_LENGTH) return null;

  const octets = parseIPv4Octets(value);
  if (octets) return octets.join(".");

  const groups = parseIPv6Groups(value);
  if (!groups) return null;

  // An IPv4-mapped IPv6 address is the same host as its IPv4 form; keep one identity for both.
  if (groups.slice(0, 5).every((group) => group === 0) && groups[5] === 0xffff) {
    return [groups[6] >> 8, groups[6] & 0xff, groups[7] >> 8, groups[7] & 0xff].join(".");
  }

  return formatIPv6(groups);
}
