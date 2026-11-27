export const locales = ["en", "ar"] as const;
export type Locale = (typeof locales)[number];

export const defaultLocale: Locale = "en";
export const localeCookie = "NEXT_LOCALE";

const rtlLocales: readonly Locale[] = ["ar"];

export function isLocale(value: unknown): value is Locale {
  return locales.includes(value as Locale);
}

export function getDirection(locale: Locale): "ltr" | "rtl" {
  return rtlLocales.includes(locale) ? "rtl" : "ltr";
}

export const localeScript = `(function(){try{var m=document.cookie.match(/(?:^|;\\s*)${localeCookie}=([^;]*)/);var l=m?decodeURIComponent(m[1]):"";if(${JSON.stringify(locales)}.indexOf(l)<0)l=${JSON.stringify(defaultLocale)};var d=document.documentElement;d.lang=l;d.dir=${JSON.stringify(rtlLocales)}.indexOf(l)<0?"ltr":"rtl"}catch(e){}})()`;
