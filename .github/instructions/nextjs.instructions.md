---
name: nextjs-frontend-rules
description: Coding rules for TypeScript, React, and Next.js App Router
applyTo: "**/*.{ts,tsx,js,jsx}"
---
# TypeScript & Next.js Implementation Standards
- **Architecture**: Strict Next.js App Router patterns. Default to Server Components for data fetching; use Client Components (`'use client'`) strictly for interactive leaf components.
- **Type Safety**: Enforce strict, explicit TypeScript interfaces. Avoid `any` or loose assertions.
- **State & Data**: Utilize native Next.js `fetch` cache tag invalidation strategies instead of pulling down external heavy state stores unless explicitly requested.
- **Styling**: Standardize on Tailwind CSS utility layouts using semantic clean class combinations.