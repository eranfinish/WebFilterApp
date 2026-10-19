import type { BrowserName } from "./protocol";

interface UserAgentBrand {
  brand: string;
  version: string;
}

export interface NavigatorLike {
  userAgent?: string;
  userAgentData?: { brands?: readonly UserAgentBrand[] };
}

/** "edge" or "chrome", from navigator.userAgentData brands, falling back to the UA string ("Edg/"). */
export function detectBrowser(nav: NavigatorLike | undefined): BrowserName {
  const brands = nav?.userAgentData?.brands;
  if (brands && brands.length > 0) {
    return brands.some((b) => /Microsoft Edge/i.test(b.brand)) ? "edge" : "chrome";
  }
  return /\bEdg(e|A|iOS)?\//.test(nav?.userAgent ?? "") ? "edge" : "chrome";
}
