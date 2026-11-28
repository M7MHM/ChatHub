"use client"

import { useLayoutEffect, type ReactNode } from "react"
import { Direction } from "radix-ui"
import { getDirection, type Locale } from "@/i18n/config"

export function DirectionProvider({ locale, children }: { locale: Locale; children: ReactNode }) {
  const dir = getDirection(locale)

  useLayoutEffect(() => {
    document.documentElement.lang = locale
    document.documentElement.dir = dir
  }, [locale, dir])

  return <Direction.Provider dir={dir}>{children}</Direction.Provider>
}
