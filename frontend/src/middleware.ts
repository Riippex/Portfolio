import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { canonicalizeIp, parseTeamAllowlist, isTeamMember } from "@/modules/security/team-allowlist";

export function middleware(request: NextRequest) {
  const pathname = request.nextUrl.pathname;

  if (
    pathname.startsWith("/_next") ||
    pathname.startsWith("/api/health") ||
    pathname === "/health" ||
    pathname === "/favicon.ico"
  ) {
    return NextResponse.next();
  }

  const stage = (
    process.env.PORTFOLIO_STAGE ||
    process.env.STAGE ||
    process.env.NEXT_PUBLIC_APP_STAGE ||
    (process.env.NODE_ENV === "production" ? "prod" : "dev")
  ).toLowerCase();

  if (stage === "dev" || stage === "development") {
    const allowlistParsed = parseTeamAllowlist(process.env.TEAM_ALLOWLIST);
    if (!allowlistParsed.valid || allowlistParsed.ips.length === 0) {
      return new NextResponse(
        JSON.stringify({ error: "Dev private access control is unconfigured or invalid." }),
        { status: 503, headers: { "content-type": "application/json" } }
      );
    }

    const rawIp =
      request.headers.get("CF-Connecting-IP")?.trim() ||
      request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ||
      request.ip ||
      "";
    const visitorIp = canonicalizeIp(rawIp);

    if (!visitorIp || !isTeamMember(visitorIp, allowlistParsed.ips)) {
      return new NextResponse(
        JSON.stringify({ error: "Private development environment access denied." }),
        { status: 403, headers: { "content-type": "application/json" } }
      );
    }
  }

  return NextResponse.next();
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};
