import type { Config } from "tailwindcss";

const config: Config = {
  content: [
    "./src/pages/**/*.{js,ts,jsx,tsx,mdx}",
    "./src/components/**/*.{js,ts,jsx,tsx,mdx}",
    "./src/app/**/*.{js,ts,jsx,tsx,mdx}",
  ],
  theme: {
    extend: {
      colors: {
        // Design system "Abismo + Neón" — vars definidas en globals.css
        abyss: {
          0: "var(--bg-0)",
          1: "var(--bg-1)",
          2: "var(--bg-2)",
          3: "var(--bg-3)",
        },
        line: { 1: "var(--border-1)", 2: "var(--border-2)" },
        ink: { 1: "var(--text-1)", 2: "var(--text-2)", 3: "var(--text-3)" },
        brand: { DEFAULT: "var(--cyan-500)", bright: "var(--cyan-300)" },
        tier: {
          s: "var(--tier-s)",
          a: "var(--tier-a)",
          b: "var(--tier-b)",
          c: "var(--tier-c)",
          d: "var(--tier-d)",
        },
        gold: "var(--gold)",
        silver: "var(--silver)",
        bronze: "var(--bronze)",
        // Compat
        background: "var(--background)",
        foreground: "var(--foreground)",
      },
      fontFamily: {
        display: "var(--font-display)",
        sans: "var(--font-body)",
        mono: "var(--font-mono)",
      },
      borderRadius: {
        badge: "6px",
        btn: "10px",
        card: "14px",
        modal: "20px",
      },
      boxShadow: {
        card: "var(--shadow-2)",
        overlay: "var(--shadow-3)",
        glow: "var(--glow-accent)",
        focus: "var(--glow-focus)",
      },
      transitionDuration: {
        fast: "120ms",
        base: "180ms",
        slow: "280ms",
      },
      transitionTimingFunction: {
        out: "cubic-bezier(0.2, 0.8, 0.3, 1)",
        snap: "cubic-bezier(0.3, 1.4, 0.4, 1)",
      },
      maxWidth: {
        container: "1200px",
      },
    },
  },
  plugins: [],
};
export default config;
