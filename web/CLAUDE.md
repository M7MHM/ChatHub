# ChatHub Web

Next.js 16 (App Router) · Tailwind CSS v4 · shadcn/ui (Radix) · next-themes. Run npm commands from `web/`.

@AGENTS.md

## Rules for all frontend work

- Read `DESIGN.md` before any UI work. Never hard-code colors (no hex/rgb in components); use tokens only (`bg-panel`, `text-brand-text`, `bg-bubble-out`…).
- Logical direction classes only: `ms-/me-/ps-/pe-/start-/end-/text-start/text-end/border-s/rounded-s…`. Never `ml-/mr-/pl-/pr-/left-/right-/text-left/text-right`.
- Only render features the backend supports (DESIGN.md section 7). Nothing from its "never render" list.
- Keep each task small. Don't add libraries that weren't asked for.
- Never commit, merge or push.
- At the end of every task, list the files created/changed with one line each on why.
