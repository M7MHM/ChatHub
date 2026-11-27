import type { ReactNode } from "react";
import {
  AtSign,
  Check,
  CheckCheck,
  Eye,
  Mail,
  Search,
  SendHorizontal,
  type LucideIcon,
} from "lucide-react";
import { getLocale, getTranslations } from "next-intl/server";
import { LanguageToggle } from "@/components/language-toggle";
import { ThemeToggle } from "@/components/theme-toggle";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";

const sectionTitle =
  "text-xs font-semibold tracking-[.08em] text-muted-foreground uppercase rtl:text-[13px] rtl:tracking-normal rtl:normal-case";

type Status = "sent" | "delivered" | "read";

function Bubble({
  out = false,
  last = false,
  time,
  status,
  statusLabel,
  children,
}: {
  out?: boolean;
  last?: boolean;
  time: string;
  status?: Status;
  statusLabel?: string;
  children: ReactNode;
}) {
  const Tick = status === "sent" ? Check : CheckCheck;

  return (
    <div
      className={cn(
        "max-w-[86%] animate-pop rounded-2xl px-3 pt-2 pb-1.5 text-[15px]/[1.5] wrap-break-word shadow-elev motion-reduce:animate-none md:max-w-[min(560px,78%)]",
        out
          ? "self-end bg-bubble-out text-bubble-out-foreground"
          : "self-start bg-bubble-in text-bubble-in-foreground",
        last && (out ? "mb-2.5 rounded-ee-md" : "mb-2.5 rounded-es-md"),
      )}
    >
      {children}
      <span
        className={cn(
          "float-end ms-3 mt-1.5 flex items-center gap-1 text-[11px] leading-4",
          out ? "text-tick" : "text-muted-foreground",
        )}
      >
        <bdi dir="ltr">{time}</bdi>
        {out && status && (
          <Tick
            aria-label={statusLabel}
            className={cn("size-4", status === "read" ? "text-tick-read" : "text-tick")}
          />
        )}
      </span>
    </div>
  );
}

function SettingRow({
  id,
  icon: Icon,
  label,
  sub,
  defaultChecked,
}: {
  id: string;
  icon: LucideIcon;
  label: string;
  sub: string;
  defaultChecked?: boolean;
}) {
  return (
    <div className="flex min-h-[68px] items-center gap-4 px-5 py-3.5">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-brand-soft text-brand-text">
        <Icon className="size-5" />
      </span>
      <label htmlFor={id} className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-[15px] font-medium">{label}</span>
        <span className="text-[13px] text-muted-foreground">{sub}</span>
      </label>
      <Switch id={id} defaultChecked={defaultChecked} />
    </div>
  );
}

export default async function StylePreviewPage() {
  const locale = await getLocale();
  const t = await getTranslations();
  const timeFormat = new Intl.DateTimeFormat(locale, {
    hour: "numeric",
    minute: "2-digit",
    numberingSystem: "latn",
  });
  const at = (hour: number, minute: number) =>
    timeFormat.format(new Date(2026, 10, 27, hour, minute));

  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-2xl flex-col gap-10 px-4 py-8 md:px-6">
      <header className="flex items-center justify-between gap-4">
        <div className="min-w-0">
          <h1 className="text-xl font-bold tracking-[-0.02em] rtl:tracking-normal">
            {t("common.appName")}
          </h1>
          <p className="text-[13px] text-muted-foreground">{t("preview.subtitle")}</p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <LanguageToggle />
          <ThemeToggle />
        </div>
      </header>

      <section className="flex flex-col gap-3">
        <h2 className={sectionTitle}>{t("preview.sections.buttons")}</h2>
        <div className="flex flex-wrap items-center gap-3">
          <Button>{t("preview.variants.default")}</Button>
          <Button variant="ghost">{t("preview.variants.ghost")}</Button>
          <Button variant="outline">{t("preview.variants.outline")}</Button>
          <Button variant="destructive">{t("preview.variants.destructive")}</Button>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <Button size="lg">{t("auth.signIn")}</Button>
          <Button size="lg" variant="outline">
            {t("auth.continueAsGuest")}
          </Button>
          <Button size="icon" variant="ghost" aria-label={t("chat.search")}>
            <Search />
          </Button>
          <Button size="round" aria-label={t("chat.send")}>
            <SendHorizontal className="rtl:-scale-x-100" />
          </Button>
        </div>
      </section>

      <section className="flex flex-col gap-3">
        <h2 className={sectionTitle}>{t("preview.sections.input")}</h2>
        <div className="flex max-w-sm flex-col gap-2">
          <label htmlFor="identifier" className="text-[13px] font-semibold">
            {t("auth.identifier.label")}
          </label>
          <div className="relative">
            <AtSign
              aria-hidden
              className="pointer-events-none absolute start-4 top-1/2 size-[18px] -translate-y-1/2 text-muted-foreground"
            />
            <Input
              id="identifier"
              autoComplete="username"
              placeholder={t("auth.identifier.placeholder")}
              className="h-[52px] rounded-xl border-input bg-field ps-[46px] pe-4 focus-visible:border-primary focus-visible:ring-4 focus-visible:ring-brand-soft md:text-[15px] dark:bg-field"
            />
          </div>
          <p className="text-[13px] text-muted-foreground">
            {t.rich("auth.identifier.hint", {
              username: "@m7moud",
              phone: "+20 100 123 4567",
              ltr: (chunks) => <bdi dir="ltr">{chunks}</bdi>,
            })}
          </p>
        </div>
      </section>

      <section className="flex flex-col gap-3">
        <h2 className={sectionTitle}>{t("preview.sections.settings")}</h2>
        <Card className="gap-0 divide-y rounded-3xl border py-0 ring-0">
          <SettingRow
            id="presence"
            icon={Eye}
            label={t("settings.presence.label")}
            sub={t("settings.presence.sub")}
            defaultChecked
          />
          <SettingRow
            id="digests"
            icon={Mail}
            label={t("settings.digests.label")}
            sub={t("settings.digests.sub")}
          />
        </Card>
      </section>

      <section className="flex flex-col gap-3">
        <h2 className={sectionTitle}>{t("preview.sections.chat")}</h2>
        <div className="overflow-hidden rounded-3xl border">
          <div className="flex flex-col gap-1 bg-panel bg-chat px-[clamp(16px,5vw,64px)] pt-6 pb-4">
            <span className="mb-3 self-center rounded-full bg-header px-3.5 py-1.5 text-xs text-muted-foreground shadow-elev">
              {t("chat.today")}
            </span>
            <Bubble time={at(10, 41)}>{t("preview.messages.m1")}</Bubble>
            <Bubble time={at(10, 42)} last>
              {t("preview.messages.m2")}
            </Bubble>
            <Bubble out time={at(10, 44)} status="delivered" statusLabel={t("chat.status.delivered")}>
              {t("preview.messages.m3")}
            </Bubble>
            <Bubble out last time={at(10, 45)} status="read" statusLabel={t("chat.status.read")}>
              {t("preview.messages.m4")}
            </Bubble>
          </div>
        </div>
      </section>
    </main>
  );
}
