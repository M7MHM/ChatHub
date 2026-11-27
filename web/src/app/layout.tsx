import { Suspense, type ReactNode } from "react";
import type { Metadata } from "next";
import { Geist, IBM_Plex_Sans_Arabic } from "next/font/google";
import { NextIntlClientProvider } from "next-intl";
import { getLocale } from "next-intl/server";
import { DirectionProvider } from "@/components/direction-provider";
import { ThemeProvider } from "@/components/theme-provider";
import { defaultLocale, getDirection, localeScript } from "@/i18n/config";
import "./globals.css";

const geist = Geist({
  variable: "--font-geist",
  subsets: ["latin"],
});

const plexArabic = IBM_Plex_Sans_Arabic({
  variable: "--font-plex-arabic",
  subsets: ["arabic"],
  weight: ["400", "500", "600", "700"],
});

export const metadata: Metadata = {
  title: "ChatHub",
  description: "Real-time messaging in Arabic and English",
};

async function LocaleProviders({ children }: { children: ReactNode }) {
  const locale = await getLocale();

  return (
    <NextIntlClientProvider>
      <DirectionProvider locale={locale}>{children}</DirectionProvider>
    </NextIntlClientProvider>
  );
}

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang={defaultLocale}
      dir={getDirection(defaultLocale)}
      className={`${geist.variable} ${plexArabic.variable}`}
      suppressHydrationWarning
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: localeScript }} />
      </head>
      <body>
        <ThemeProvider>
          <Suspense>
            <LocaleProviders>{children}</LocaleProviders>
          </Suspense>
        </ThemeProvider>
      </body>
    </html>
  );
}
