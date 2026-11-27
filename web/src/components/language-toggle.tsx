"use client"

import { useTransition } from "react"
import { Globe } from "lucide-react"
import { useLocale, useTranslations } from "next-intl"
import { useRouter } from "next/navigation"
import { Button } from "@/components/ui/button"
import { localeCookie } from "@/i18n/config"

export function LanguageToggle() {
  const t = useTranslations("language")
  const locale = useLocale()
  const router = useRouter()
  const [isPending, startTransition] = useTransition()
  const target = locale === "ar" ? "en" : "ar"

  function switchLanguage() {
    document.cookie = `${localeCookie}=${target}; path=/; max-age=31536000; samesite=lax`
    startTransition(() => router.refresh())
  }

  return (
    <Button variant="ghost" lang={target} disabled={isPending} onClick={switchLanguage}>
      <Globe />
      {t("switch")}
    </Button>
  )
}
