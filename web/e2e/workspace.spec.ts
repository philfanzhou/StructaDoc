import { expect, test } from '@playwright/test'

const administratorUsername = process.env.STRUCTADOC_E2E_ADMIN_USERNAME ?? 'structadoc-admin'
const administratorPassword = process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password'

test('administrator can use the document workspace and administration area', async ({ page }) => {
  // The workspace and the administration area are distinct routes of one Host, and both send an
  // unauthenticated visitor to their own sign-in page.
  await page.goto('/')
  await expect(page).toHaveURL(/\/signin/)

  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin/)
  await page.locator('form input[name="username"]').fill(administratorUsername)
  await page.locator('form input[type="password"]').fill(administratorPassword)
  await page.locator('form button').click()

  await expect(page).toHaveURL(/\/admin$/)
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()
  await expect(page.getByText('PROVIDERS', { exact: true })).toBeVisible()
  await expect(page.getByText('API CLIENTS', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/administration.png', fullPage: true })

  await page.getByRole('link', { name: '文档工作台' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByText('WORKSPACE', { exact: true })).toBeVisible()

  await page.locator('input[type="file"]').setInputFiles({
    name: 'e2e-sample.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% StructaDoc browser contract sample\n%%EOF\n'),
  })
  await expect(page.getByText('e2e-sample.pdf', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/workspace.png', fullPage: true })

  // A deep administration link survives a full reload, so the Host serves the SPA shell for
  // client-side routes rather than only for `/`.
  await page.goto('/admin')
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()
})
