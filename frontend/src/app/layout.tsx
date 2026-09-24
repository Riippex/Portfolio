import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Rafael Patiño — AI Systems Engineer",
  description: "AI systems, autonomous agents, computer vision, and cloud engineering.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="en" className="h-full antialiased">
      <body className="min-h-full flex flex-col">{children}</body>
    </html>
  );
}
