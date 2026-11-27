# ChatHub Web — Design System

This is the visual source of truth for the ChatHub frontend (`web/`). Follow it for every screen and component.
Layouts and screens can change freely. **The look must not:** the dark-first purple palette, the radii, the typography and the motion defined here.

Stack: Next.js (App Router) · Tailwind CSS v4 · shadcn/ui (Radix) · lucide-react icons · next-themes · Sonner toasts · vaul Drawer (via shadcn `drawer`).

---

## 1. Theme tokens — `app/globals.css`

Use this file as is. Dark is the default theme (`next-themes`: `attribute="class"`, `defaultTheme="dark"`, `enableSystem={false}`).
Never hard-code a hex color in a component. Always use a token (`bg-panel`, `text-brand-text`, `bg-bubble-out`…).

```css
@import "tailwindcss";
@import "tw-animate-css";

@custom-variant dark (&:is(.dark *));

@theme inline {
  /* shadcn tokens */
  --color-background: var(--background);
  --color-foreground: var(--foreground);
  --color-card: var(--card);
  --color-card-foreground: var(--card-foreground);
  --color-popover: var(--popover);
  --color-popover-foreground: var(--popover-foreground);
  --color-primary: var(--primary);
  --color-primary-foreground: var(--primary-foreground);
  --color-secondary: var(--secondary);
  --color-secondary-foreground: var(--secondary-foreground);
  --color-muted: var(--muted);
  --color-muted-foreground: var(--muted-foreground);
  --color-accent: var(--accent);
  --color-accent-foreground: var(--accent-foreground);
  --color-destructive: var(--destructive);
  --color-border: var(--border);
  --color-input: var(--input);
  --color-ring: var(--ring);
  --color-sidebar: var(--sidebar);
  --color-sidebar-foreground: var(--sidebar-foreground);
  --color-sidebar-border: var(--sidebar-border);
  --color-sidebar-accent: var(--sidebar-accent);
  --color-sidebar-accent-foreground: var(--sidebar-accent-foreground);
  --color-sidebar-primary: var(--sidebar-primary);
  --color-sidebar-primary-foreground: var(--sidebar-primary-foreground);
  --color-sidebar-ring: var(--sidebar-ring);

  /* ChatHub tokens */
  --color-panel: var(--panel);               /* chat + page background */
  --color-header: var(--header);             /* top bars, composer field, popovers */
  --color-field: var(--field);               /* search, chips, segmented, inputs */
  --color-surface-active: var(--surface-active); /* selected conversation */
  --color-brand-strong: var(--brand-strong); /* primary hover */
  --color-brand-soft: var(--brand-soft);     /* icon tiles, focus halo, active chip */
  --color-brand-text: var(--brand-text);     /* links, active labels, "typing…" */
  --color-bubble-out: var(--bubble-out);
  --color-bubble-out-foreground: var(--bubble-out-foreground);
  --color-bubble-in: var(--bubble-in);
  --color-bubble-in-foreground: var(--bubble-in-foreground);
  --color-tick: var(--tick);
  --color-tick-read: var(--tick-read);
  --color-online: var(--online);

  --radius-sm: 8px;    /* small chips inside things */
  --radius-md: 10px;   /* segmented items, emoji cells */
  --radius-lg: 12px;   /* buttons, icon buttons, row icons, form inputs */
  --radius-xl: 14px;   /* auth inputs, search, segmented track */
  --radius-2xl: 18px;  /* message bubbles, theme tiles, popovers */
  --radius-3xl: 20px;  /* cards */
  --radius-4xl: 28px;  /* auth art panel */

  --font-sans: var(--font-geist), var(--font-plex-arabic), system-ui, sans-serif;
  --font-arabic: var(--font-plex-arabic), var(--font-geist), system-ui, sans-serif;

  --animate-pop: pop 0.18s ease-out;
  --animate-blink: blink 1.2s infinite ease-in-out;
}

:root {
  /* LIGHT */
  --background: #f3f1f8;
  --foreground: #17131f;
  --card: #ffffff;
  --card-foreground: #17131f;
  --popover: #ffffff;
  --popover-foreground: #17131f;
  --primary: #6a2fe0;
  --primary-foreground: #ffffff;
  --secondary: #ffffff;
  --secondary-foreground: #17131f;
  --muted: #f1eef7;
  --muted-foreground: #6b6479;
  --accent: #f5f2fb;            /* hover surface */
  --accent-foreground: #17131f;
  --destructive: #f43f5e;
  --border: #e6e1ef;
  --input: #e6e1ef;
  --ring: #6a2fe0;
  --sidebar: #ffffff;
  --sidebar-foreground: #17131f;
  --sidebar-border: #e6e1ef;
  --sidebar-accent: #f5f2fb;
  --sidebar-accent-foreground: #17131f;
  --sidebar-primary: #6a2fe0;
  --sidebar-primary-foreground: #ffffff;
  --sidebar-ring: #6a2fe0;

  --panel: #efecf6;
  --header: #ffffff;
  --field: #f1eef7;
  --surface-active: #ece5fb;
  --brand-strong: #5a23c9;
  --brand-soft: rgba(106, 47, 224, 0.1);
  --brand-text: #5a23c9;
  --bubble-out: #6a2fe0;
  --bubble-out-foreground: #ffffff;
  --bubble-in: #ffffff;
  --bubble-in-foreground: #17131f;
  --tick: rgba(255, 255, 255, 0.7);
  --tick-read: #8ae8ff;
  --online: #22c55e;
  --pattern: rgba(106, 47, 224, 0.06);
  --glow: radial-gradient(1200px 600px at 85% -10%, rgba(106, 47, 224, 0.08), transparent 60%);
  --elev: 0 1px 2px rgba(23, 19, 31, 0.08);
  --elev-pop: 0 16px 40px rgba(23, 19, 31, 0.14);
}

.dark {
  /* DARK — default */
  color-scheme: dark;
  --background: #08070c;
  --foreground: #eeebf5;
  --card: #100e16;
  --card-foreground: #eeebf5;
  --popover: #131119;
  --popover-foreground: #eeebf5;
  --primary: #7b3ff2;
  --primary-foreground: #ffffff;
  --secondary: #1b1824;
  --secondary-foreground: #eeebf5;
  --muted: #1a1722;
  --muted-foreground: #8e879d;
  --accent: #1f1b29;
  --accent-foreground: #eeebf5;
  --destructive: #f43f5e;
  --border: #231f2d;
  --input: #231f2d;
  --ring: #7b3ff2;
  --sidebar: #100e16;
  --sidebar-foreground: #eeebf5;
  --sidebar-border: #231f2d;
  --sidebar-accent: #1f1b29;
  --sidebar-accent-foreground: #eeebf5;
  --sidebar-primary: #7b3ff2;
  --sidebar-primary-foreground: #ffffff;
  --sidebar-ring: #7b3ff2;

  --panel: #0b0a10;
  --header: #131119;
  --field: #1a1722;
  --surface-active: #251f35;
  --brand-strong: #6a2fe0;
  --brand-soft: rgba(123, 63, 242, 0.16);
  --brand-text: #b597ff;
  --bubble-out: #5f27cd;
  --bubble-out-foreground: #ffffff;
  --bubble-in: #1b1824;
  --bubble-in-foreground: #eeebf5;
  --tick: rgba(255, 255, 255, 0.62);
  --tick-read: #7fe3ff;
  --online: #22c55e;
  --pattern: rgba(123, 63, 242, 0.05);
  --glow: radial-gradient(1200px 600px at 85% -10%, rgba(123, 63, 242, 0.12), transparent 60%);
  --elev: 0 1px 0 rgba(0, 0, 0, 0.35);
  --elev-pop: 0 16px 40px rgba(0, 0, 0, 0.35);
}

@layer base {
  * { @apply border-border outline-ring/50; }
  html { -webkit-tap-highlight-color: transparent; }
  body {
    @apply bg-background text-foreground antialiased;
    font-family: var(--font-sans);
    transition: background-color .2s ease, color .2s ease;
  }
  :lang(ar) body { font-family: var(--font-arabic); }
  :focus-visible { outline: 2px solid var(--brand-text); outline-offset: 2px; }
}

@layer utilities {
  .shadow-elev { box-shadow: var(--elev); }
  .shadow-pop { box-shadow: var(--elev-pop); }
  .bg-glow { background-image: var(--glow); }
  /* chat wallpaper: glow + dotted pattern (togglable in Settings → add .no-pattern on <html>) */
  .bg-chat {
    background-image: var(--glow), radial-gradient(var(--pattern) 1.2px, transparent 1.2px);
    background-size: auto, 22px 22px;
  }
  .no-pattern .bg-chat { background-image: var(--glow); }
  /* auth art panel + profile hero */
  .bg-brand-art {
    background:
      repeating-linear-gradient(120deg, rgba(255,255,255,.035) 0 30px, transparent 30px 60px),
      radial-gradient(600px 400px at 100% 100%, rgba(167,119,255,.45), transparent 70%),
      linear-gradient(155deg, #0e0524 0%, #2a0d6b 55%, #5f27cd 100%);
  }
  .bg-brand-hero {
    background:
      repeating-linear-gradient(120deg, rgba(255,255,255,.045) 0 16px, transparent 16px 34px),
      linear-gradient(135deg, #1d0b45 0%, #4c1d95 45%, #7b3ff2 100%);
  }
  .scrollbar-thin { scrollbar-width: thin; scrollbar-color: var(--border) transparent; }
}

@keyframes pop { from { opacity: 0; transform: translateY(6px); } }
@keyframes blink { 0%, 80%, 100% { opacity: .3; } 40% { opacity: 1; } }
```

Fonts (in `app/layout.tsx`): `next/font/google` → `Geist` as `--font-geist`, `IBM_Plex_Sans_Arabic` (weights 400/500/600/700, subset `arabic`) as `--font-plex-arabic`. Put both variables on `<html>`.

---

## 2. Visual language

**Feel:** dark, calm, high contrast. Purple is the only brand color, used for primary actions, outgoing bubbles, unread badges, active states and focus. Everything else is neutral and tinted slightly purple.

**Typography**
| Use | Size / weight |
|---|---|
| Auth heading | 30px / 700, tracking -0.03em (mobile 26px) |
| Page title, brand | 20px / 700, tracking -0.02em |
| Peer name (chat header) | 16px / 600 |
| Conversation name | 15px / 600 |
| Body, message text | 15px / 400, line-height 1.5 (user setting: 14 / 15 / 17 → `--msg-size`) |
| Preview, row label | 14–15px / 500 |
| Secondary, hints, errors | 13px |
| Timestamps, badges | 11–12px / 600 for badges |
| Section title | 12px / 600 uppercase, tracking .08em, muted. **In Arabic: 13px, no uppercase, no tracking** |

Negative tracking is for Latin only. Add `rtl:tracking-normal` / `:lang(ar)` overrides on every heading with tracking.

**Radii:** buttons and icon buttons `rounded-lg` (12) · inputs `rounded-xl` (14) · cards `rounded-3xl` (20) · bubbles `rounded-2xl` (18) · dialogs 22px · avatars, badges, chips, round buttons, composer field `rounded-full`.

**Elevation:** almost flat. Use `shadow-elev` on bubbles, date pills and the composer field, and `shadow-pop` on popovers, emoji tray and menus. Dialogs: `0 24px 60px rgba(0,0,0,.45)` + backdrop `rgba(4,3,8,.65)` with `backdrop-blur-sm`.

**Motion:** 150ms color/background transitions on interactive elements · `animate-pop` (fade + 6px rise) when a message, card, form or auth card appears · `active:scale-[.94]` on round buttons · typing dots `animate-blink` with 0.15s stagger. Respect `prefers-reduced-motion`.

**Icons:** lucide-react only, stroke 2, 20px default (18 in inputs/search, 16 in meta/ticks). Direction-bearing icons (send, back, chevrons, logout) get `rtl:-scale-x-100`.

---

## 3. Component recipes (shadcn overrides)

Install with the shadcn CLI, then change the defaults in `components/ui/*` to match these. Do not wrap them in new abstractions.

- **Button.** `default` = `bg-primary hover:bg-brand-strong text-primary-foreground h-[42px] px-[18px] rounded-lg text-sm font-semibold`. `ghost` = transparent with `hover:bg-accent`. `outline` = `bg-card border` (used for the "Continue as guest" button). `destructive` = rose. Size `lg` = `h-[52px] rounded-xl text-[15px]` (auth). Size `icon` = `size-10 rounded-lg text-muted-foreground hover:bg-accent hover:text-foreground`. Size `round` = `size-[52px] rounded-full` (send and mic; 48px on mobile).
- **Input (auth).** `h-[52px] rounded-xl bg-field border border-input ps-[46px]` with a leading icon at `start-4`. Focus: `border-primary ring-4 ring-brand-soft`. Invalid: `border-destructive ring-destructive/15`. An error message (13px destructive) or hint (13px muted) goes under it. Labels are 13px / 600.
- **Search.** `h-11 rounded-xl bg-field`, 18px icon, `focus-within:border-primary`.
- **Chips (All / Unread / Groups).** Use ToggleGroup, `h-8 px-3.5 rounded-full bg-field text-muted-foreground text-[13px]`. Active: `bg-brand-soft text-brand-text`.
- **Segmented (Small / Default / Large, EN / عربي).** A track `p-1 rounded-xl bg-field border` with items `h-[34px] rounded-md`. Active item: `bg-primary text-white`.
- **Switch.** 48×28 track, 20px thumb. On: `bg-primary`.
- **Avatar.** 50px in the list, 42px small, 60px large, 116px on the profile hero (4px ring in card color). Fallback: initials on a deterministic color chosen from `#7b3ff2 #0ea5e9 #f59e0b #ec4899 #10b981 #a855f7 #22c55e #e11d48` by hashing the id. Presence dot: 13px `bg-online` with a 2px ring in the parent background, at `bottom-px end-px`.
- **Badge (unread).** `min-w-[22px] h-[22px] px-[7px] rounded-full bg-primary text-white text-xs font-semibold`. A muted conversation uses `bg-muted-foreground/40` instead.
- **Card / settings row.** Card: `bg-card border rounded-3xl overflow-hidden`. Row: `min-h-[68px] px-5 py-3.5 gap-4` with dividers between rows. Each row has a 40px `rounded-lg bg-brand-soft text-brand-text` icon tile, then a label (15/500) and a sub (13 muted), then a trailing switch, segmented control or chevron. A danger row uses rose text and a rose-tinted icon tile.
- **Dialog / AlertDialog.** `max-w-[400px] p-7 rounded-[22px] bg-header`. A 48px icon tile sits at the top, then the title (19/700), body (15 muted), and actions aligned to the end. Use AlertDialog for destructive confirmations: log out, delete account, clear chat, leave group, delete message, block.
- **Toast (Sonner).** An inverted pill: `bg-foreground text-background rounded-full px-[18px] py-[11px] text-sm`, bottom-center. On mobile it sits above the tab bar.
- **Skeleton.** `bg-field` pulse. Every list and the message pane get skeletons that match the real layout, not spinners.

---

## 4. Chat UI specifics

- **Conversation item.** `p-3 gap-3.5 rounded-2xl`, hover `bg-accent`, selected `bg-surface-active`. Row 1 has the name (truncate) and the time (12px muted; with unread it is `text-brand-text font-semibold`). Row 2 has the preview: ticks for my last message, a "typing…" state in `text-brand-text`, a sender prefix in groups, and the badge at the end. It also shows mute and voice/image/file glyphs.
- **Message bubble.** `max-w-[min(560px,78%)]` (86% on mobile) `px-3 pt-2 pb-1.5 rounded-2xl shadow-elev`, wraps long words. Incoming: `bg-bubble-in self-start`. Outgoing: `bg-bubble-out text-white self-end`. The last bubble of a consecutive run gets a tail corner (`rounded-es-md` incoming / `rounded-ee-md` outgoing) and `mb-2.5`. Bubbles in the same run use a `gap-1` stack. In groups, the sender name (13/600, colored with the sender's avatar color) shows on the first incoming bubble of a run.
- **Meta.** Time (11px) and ticks float to the inline end inside the bubble (`float-end ms-3 mt-1.5`). Ticks: one check = sent, two checks = delivered (`text-tick`), two checks in `text-tick-read` = read. Show "edited" before the time when a message was edited.
- **Reply quote.** Inside the bubble, above the text: `rounded-lg bg-black/15` (incoming: `bg-foreground/5`), a 3px `border-s` in the brand color, the sender name (13/600) and one line of the quoted text. Clicking it scrolls to the original.
- **Deleted message.** Italic muted text "This message was deleted" with a ban icon. No actions.
- **Attachments.** Image: `rounded-xl` inside the bubble with max height 320, and the meta overlays the bottom-end on a dark gradient. File: a row with a 40px icon tile, the name (truncate), size and type. Voice: a 36px round play button, a waveform of 3px bars (opacity .55, played part 1), and the duration (12px).
- **Date separator.** A centered pill `px-3.5 py-1.5 rounded-full bg-header text-xs text-muted-foreground shadow-elev`: Today / Yesterday / weekday / date.
- **Typing.** An incoming bubble with 3 dots (7px). The header status and the list preview switch to "typing…" in `text-brand-text`.
- **Chat header.** `min-h-[72px] bg-header border-b` with the avatar, name and status (green dot + "online", or "last seen …", or "typing…", or "N members" for groups). Actions: search in chat (optional) and ⋮ menu. **No call or video buttons.**
- **Composer.** `p-3 pb-4 gap-2`. The field is a `rounded-full bg-header border shadow-elev min-h-[52px] p-1.5` pill containing emoji, a textarea (auto-grow, max 5 lines) and attach. The send button replaces the mic when there is text. A reply or edit preview bar sits above the field with an × button. Recording shows a red dot, a timer and a pulsing red mic. **Every flex child in the composer gets `min-w-0`.** (The template overflowed on 390px screens because of this.)
- **Messages pane.** `bg-panel bg-chat px-[clamp(16px,5vw,64px)] pt-6 pb-4`. It is anchored to the bottom, older pages load on scroll-up (cursor paging), and the scroll position is kept. A floating "↓" button with an unread count appears when the view is scrolled up.

---

## 5. Responsive — two different experiences

Breakpoint: **`md` (768px)**. Below it is the mobile app; at `md` and above it is the desktop app. Don't just shrink the desktop layout. Mobile has its own navigation and interaction patterns.

### Desktop / laptop (≥ md)
- **Shell:** a grid with an 84px icon **rail** (Chats with total unread badge, Profile, Settings; 68px buttons with a 52×34 pill icon and an 11px label; active = `bg-brand-soft text-brand-text`), a **list column** `minmax(320px,380px)` and the **chat pane**.
- **Group / contact info** opens as a third column (360px) at `xl` and as a right Sheet below `xl`.
- **Message actions:** a hover button (chevron) on the bubble plus a right-click ContextMenu with Reply, Copy, Edit, Delete and Info.
- **Menus:** DropdownMenu and Dialog.
- **Shortcuts:** Enter sends and Shift+Enter adds a new line · Esc closes reply/edit, then the chat · ↑ in an empty composer edits my last message.
- **Empty chat pane:** a centered brand mark, a short line and a "New chat" button.

### Mobile (< md)
- **One screen at a time, driven by routes:** `/chats` shows the list full screen, and `/chats/[id]` shows the chat full screen with a back button. Profile and settings are full pages. The browser back button and swipe-back must work.
- **Bottom tab bar** (Chats, Profile, Settings) with `pb-[env(safe-area-inset-bottom)]`. It is **hidden inside an open chat** and on auth screens.
- **New chat** is a floating action button (56px round, primary) at the bottom-end of the list, above the tab bar.
- **Message actions:** long-press (~400ms, with haptic `navigator.vibrate` if available) opens a bottom **Drawer** with the actions and highlights the bubble. Swipe a bubble toward the inline start to reply (optional; long-press is enough for v1).
- **All menus and pickers become bottom Drawers** (⋮ menu, attach menu, member actions, language, confirmations can stay as AlertDialog).
- **Sizes:** composer round buttons 48px, chat header `min-h-16 px-3`, list item padding 12px, page padding `px-3.5`.
- **Viewport:** use `h-dvh`, never `100vh`. The composer stays visible above the on-screen keyboard (`interactive-widget=resizes-content` in the viewport meta).
- **Text inputs use `text-base` (16px) on mobile** so iOS Safari doesn't zoom. Use 15px from `md`.
- **Touch targets:** at least 40×40, and nothing important hover-only.

Build mobile and desktop layouts as separate shell components (`MobileShell`, `DesktopShell`) chosen by a `useIsMobile()` hook (matchMedia on `(max-width: 767px)`). Shared pieces (bubble, conversation item, composer, forms) are the same components in both.

### Auth screens
- **≥ 860px:** a split layout. On the left (start), the `bg-brand-art` panel `rounded-4xl m-4` with the brand, a big headline, a short copy and a tiny animated chat preview. On the right, the form card `max-w-[420px]` centered, with the language and theme buttons top-end.
- **< 860px:** the art panel is hidden and a small brand row shows above the form.
- **The footer under the art panel must not claim encryption.** Use a true line, e.g. "Real-time messaging in Arabic and English" or "Try it instantly as a guest".

---

## 6. RTL & i18n

- Two locales, `en` and `ar`. Set `<html lang dir>` from the locale (`ar` → `dir="rtl"`). Radix needs `<DirectionProvider dir>`. Keep all strings in `messages/en.json` and `messages/ar.json`. No hard-coded UI text.
- **Logical utilities only:** `ms-/me-/ps-/pe-/start-/end-/text-start/text-end/border-s/rounded-s…`. Never `ml-/mr-/pl-/pr-/left-/right-/text-left`.
- Wrap phone numbers, usernames (`@m7moud`), emails and times in `<bdi dir="ltr">` inside Arabic text.
- Times and dates go through `Intl.DateTimeFormat(locale)`. Arabic uses ص/م, and digits can stay Latin (`numberingSystem: "latn"`) for consistency with the template.
- Language and theme toggles are reachable from the auth screens, the sidebar top bar (desktop) and Settings.

---

## 7. Screens — only what the backend supports

| Area | Screens / UI | API |
|---|---|---|
| Auth | Login (identifier = email, phone or username + password) · Register (display name, **username** with live availability, email **or** Egyptian phone, password + confirm, strength meter) · **Continue as guest** · Forgot password · Reset password (token from link) · Verify email (token from link) | `auth/*`, `users/username-availability` |
| Banners | Email not verified (resend button) · Guest: "Guest session — expires in Xh" | `users/me`, `auth/email-verification` |
| Chats | List with filters All / Unread / Groups and search · Chat · New direct chat (user search) · New group (name, image, members) | `conversations`, `users/search` |
| Messages | Text, image, file and voice · reply · edit · delete for everyone · read/delivered ticks · typing · presence | `conversations/{id}/messages`, `messages/{id}`, `files`, SignalR |
| Chat menu (⋮) | Contact/group info · Mute / Unmute · Clear chat · Block / Unblock (direct) · Leave group | `conversations/{id}/mute|clear|leave`, `users/{id}/block` |
| Group info | Image, name, edit (admins) · members with roles · add member · make/remove admin · remove member · transfer ownership (owner) | `conversations/{id}`, `/members`, `/role`, `/transfer-ownership` |
| Profile | Avatar (upload/remove), display name, bio, username (change limited to once per 14 days; show the next allowed date) | `users/me`, `users/me/username`, `files` |
| Settings | Appearance (theme tiles, message text size, wallpaper pattern — local) · Language · Notifications (email digests → `email-preferences`) · Privacy (hide online & last seen → `users/me/privacy`, with a note that hiding yours hides others' from you) · Blocked users · Change password · Log out · Delete account | as listed |

**Not in the product — never render:** voice/video calls, end-to-end encryption claims, a read-receipts toggle, pinned chats, reactions, stories, forwarding, "share profile link". Add them only when the backend supports them.

Guest accounts: hide or disable username change, email/phone, verification, change password and forgot password. The API answers `403 Guest.Restricted` for these.

---

## 8. States checklist (every screen)

Loading (skeleton) · empty (icon tile + one line + action) · error (inline message + retry, toast for transient errors) · offline/reconnecting (thin bar under the header: "Connecting…" while SignalR reconnects) · disabled buttons while submitting (spinner inside the button, same width). Map API error codes (e.g. `Guest.Restricted`, `Guest.CapacityReached`, validation errors) to friendly translated messages.