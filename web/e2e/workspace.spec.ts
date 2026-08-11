import { expect, test } from '@playwright/test'

const administratorUsername = process.env.STRUCTADOC_E2E_ADMIN_USERNAME ?? 'structadoc-admin'
const administratorPassword = process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password'

// The Host answers an unmatched `/api` path with 404, so a Provider pointed at one is a service that
// is reachable and refuses the submission. That is deliberate: it needs no second container, and a
// 404 is a permanent failure, so the run reaches a final status immediately instead of retrying for
// a minute. What it proves is the part that is image-specific — that the resident Worker in the
// published image claims a queued run, leases it, calls the Provider over HTTP, and records a final
// status. The success branch is covered against a real socket by ParseExecutionEndToEndTests.
const refusingProviderBaseUrl = 'http://127.0.0.1:8080/api/v1/system'

test('administrator can use the document workspace and administration area', async ({ page }) => {
  // A retry runs against the deployment the previous attempt already wrote to, and nothing here is
  // cleaned up afterwards. Names carry a run stamp so an attempt never matches a leftover.
  const stamp = Date.now().toString(36)
  const providerName = `Contract provider ${stamp}`
  const correctedName = `Corrected provider ${stamp}`
  const fileName = `e2e-sample-${stamp}.pdf`

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
  // Where documents and business data live are settings like any other, and a deployment that could
  // not reach them from here would have to be recreated to move either.
  await expect(page.getByText('STORAGE', { exact: true })).toBeVisible()
  await expect(page.getByText('DATABASE', { exact: true })).toBeVisible()

  // Which build is running has to be readable by an administrator who never opens a terminal, which
  // is the whole reason it is on the page rather than only in `docker image inspect`. What the page
  // owes is that the string is the service's own, in full, copyable: the abbreviation next to it is
  // for reading. Whether that string names a commit depends on the build arguments rather than on
  // this page, and the container job checks it where it is decided.
  const reported = await page.evaluate(async () => (await (await fetch('/api/v1/system/info')).json()).version)
  expect(reported).toBeTruthy()
  await expect(page.locator('.page-header code')).toHaveAttribute('title', reported)

  const providers = page.locator('section').filter({ has: page.getByText('PROVIDERS', { exact: true }) })
  // A deployment with no Provider cannot parse anything, so the administration area has to be able
  // to create one and mark it default without a command line.
  await providers.getByText('新增提供方').click()
  const createForm = providers.locator('details .form-grid')
  await createForm.locator('input').first().fill(providerName)

  // The hosted service has one published address, and an administrator who has to retype it from
  // memory is being asked to get it wrong. Selecting the type fills it in, and the value comes from
  // the service rather than from this bundle, so this also proves the descriptors were served.
  await createForm.locator('select').selectOption('mineru-cloud')
  await expect(createForm.locator('input[type="url"]')).toHaveValue('https://mineru.net')
  // Each type reads one of the two optional settings, so the other is not offered at all: a field
  // that changes nothing about the request is worse than a missing one.
  await expect(createForm.getByText('模型（可选）')).toBeVisible()
  await expect(createForm.getByText('后端（可选）')).toBeHidden()

  await createForm.locator('select').selectOption('mineru-local')
  // A self-hosted address is the deployment's own, so switching away from the hosted type takes the
  // suggestion back out rather than leaving a wrong address that looks deliberate.
  await expect(createForm.locator('input[type="url"]')).toHaveValue('')
  await createForm.locator('input[type="url"]').fill(refusingProviderBaseUrl)
  await createForm.locator('input[type="checkbox"]').check()
  await createForm.getByRole('button', { name: '创建' }).click()

  const providerRow = providers.locator('.admin-list > div').filter({ hasText: providerName })
  await expect(providerRow.getByText('默认', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/administration.png', fullPage: true })

  await page.getByRole('link', { name: '文档工作台' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByText('WORKSPACE', { exact: true })).toBeVisible()

  await page.locator('input[type="file"]').setInputFiles({
    name: fileName,
    mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% StructaDoc browser contract sample\n%%EOF\n'),
  })
  await expect(page.getByText(fileName, { exact: true })).toBeVisible()

  // Starting a parse names no Provider, so this only works because the default set above is in
  // force. It is the step that fails when a deployment has configuration but no default.
  await page.getByText(fileName, { exact: true }).click()
  await page.getByRole('button', { name: '开始新解析' }).click()
  const runStatus = page.locator('.run-list .status').first()
  await expect(runStatus).toBeVisible()
  await page.locator('.run-list > button').first().click()

  // Nothing is clicked from here. The workspace polls while work is unfinished, so reaching a final
  // status on screen proves the Worker ran and the auto-refresh reported it. The error text
  // appearing without a second click proves the poll kept the selected run rather than dropping it.
  await expect(page.locator('.auto-refresh')).toBeVisible()
  await expect(runStatus).toHaveText('失败', { timeout: 60_000 })
  await expect(page.locator('.inline-error')).toBeVisible()
  await expect(page.locator('.auto-refresh')).toBeHidden()
  await page.screenshot({ path: 'test-results/workspace.png', fullPage: true })

  // A deep administration link survives a full reload, so the Host serves the SPA shell for
  // client-side routes rather than only for `/`.
  await page.goto('/admin')
  await expect(page.getByText('ADMINISTRATION', { exact: true })).toBeVisible()

  // A configuration that has been used keeps its parse history, so it is disabled rather than
  // deleted. Both answers have to reach the administrator rather than fail silently.
  page.once('dialog', dialog => dialog.accept())
  await providerRow.getByRole('button', { name: '删除' }).click()
  await expect(page.locator('.toast.error')).toBeVisible()
  await expect(providerRow).toBeVisible()

  // Correcting a configuration in place is the other half: without it a wrong address or credential
  // could only be replaced by creating another Provider.
  await providerRow.getByRole('button', { name: '编辑' }).click()
  const editor = providers.locator('.provider-editor')
  await editor.locator('input').first().fill(correctedName)
  await editor.getByRole('button', { name: '保存' }).click()
  await expect(providers.getByText(correctedName)).toBeVisible()
})
