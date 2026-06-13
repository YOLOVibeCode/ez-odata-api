import { test, expect, request, type Page } from "@playwright/test";

// Core journey smoke (spec 10). Setup is a one-time, server-side heavy operation
// (Argon2id), so we complete it via the API once in beforeAll, then exercise the
// real UI through login + navigation. This keeps the UI assertions deterministic.
const ADMIN = { email: "playwright@example.com", displayName: "Playwright Admin", password: "playwright-strong-pass-1" };

test.beforeAll(async ({ baseURL }) => {
  const ctx = await request.newContext({ baseURL });
  const status = await (await ctx.get("/system/setup")).json();
  if (status.required) {
    await ctx.post("/system/setup", { data: ADMIN });
  }
  await ctx.dispose();
});

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN.email);
  await page.getByLabel("Password").fill(ADMIN.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("link", { name: "Services" })).toBeVisible({ timeout: 15_000 });
}

test("login lands on the dashboard", async ({ page }) => {
  await signIn(page);
  await expect(page.getByText("Dashboard").first()).toBeVisible();
});

test("can open the new-service wizard", async ({ page }) => {
  await signIn(page);
  await page.getByRole("link", { name: "Services" }).click();
  await page.getByRole("button", { name: /New service/ }).click();
  await expect(page.getByText("Connector")).toBeVisible();
});

test("roles page lists the seeded roles", async ({ page }) => {
  await signIn(page);
  await page.getByRole("link", { name: "Roles" }).click();
  await expect(page.getByText("read-only-all")).toBeVisible();
});

test("login rejects bad credentials", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN.email);
  await page.getByLabel("Password").fill("wrong-password-here-1");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("Invalid credentials.")).toBeVisible();
});
