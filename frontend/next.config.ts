import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Staff invite (ED-31): the accept page never leaks its URL through the
  // Referer header. The page's own layout adds the matching meta tag.
  async headers() {
    return [
      {
        source: "/accept-invite",
        headers: [{ key: "Referrer-Policy", value: "no-referrer" }],
      },
    ];
  },
};

export default nextConfig;
