import { expect, test } from '@playwright/test'

const administratorEmail = process.env.STRUCTADOC_E2E_ADMIN_EMAIL ?? 'admin@example.test'
const administratorPassword = process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password'

test('administrator can use the document workspace and administration area', async ({ page }) => {
  await page.goto('/')

  await expect(page.locator('form input[type="email"]')).toBeVisible()
  await page.locator('form input[type="email"]').fill(administratorEmail)
  await page.locator('form input[type="password"]').fill(administratorPassword)
  await page.locator('form button').click()

  await expect(page.getByText('WORKSPACE', { exact: true })).toBeVisible()

  await page.locator('input[type="file"]').setInputFiles({
    name: 'e2e-sample.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% StructaDoc browser contract sample\n%%EOF\n'),
  })
  await expect(page.getByText('e2e-sample.pdf', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/workspace.png', fullPage: true })

  await page.locator('aside nav button').nth(1).click()
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()
  await expect(page.getByText('PROVIDERS', { exact: true })).toBeVisible()
  await expect(page.getByText('API CLIENTS', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/administration.png', fullPage: true })
})
