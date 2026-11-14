import { expect, test } from '@playwright/test'
import { createServer } from 'node:http'
import type { AddressInfo } from 'node:net'

test('an OIDC administrator signs out through the authority in the browser', async ({ page }) => {
  const token = 'test-antiforgery-token'
  let authenticated = true
  let authorityRequest: { method?: string; cookie?: string; csrf?: string | string[] } | undefined
  const authority = createServer((request, response) => {
    if (request.url !== '/oauth2/session/end') {
      response.writeHead(404).end()
      return
    }
    authorityRequest = {
      method: request.method,
      cookie: request.headers.cookie,
      csrf: request.headers['x-csrf-token'],
    }
    response.writeHead(200, { 'Content-Type': 'text/html' })
    response.end('<h1>Signed out at the authority</h1>')
  })
  await new Promise<void>(resolve => authority.listen(0, '127.0.0.2', resolve))
  const endSessionUrl = `http://127.0.0.2:${(authority.address() as AddressInfo).port}/oauth2/session/end`

  try {
    await page.context().addCookies([{ name: 'test-sso', value: 'active', url: endSessionUrl, sameSite: 'Lax' }])
    await page.route('**/api/v1/**', async route => {
      const request = route.request()
      const url = new URL(request.url())
      if (url.pathname === '/api/v1/session') {
        return route.fulfill({ json: {
          authenticated, subjectType: 'user', isAdministrator: authenticated, oidcEnabled: true,
          setupRequired: false, displayName: 'OIDC administrator',
        } })
      }
      if (url.pathname === '/api/v1/admin/antiforgery') {
        return route.fulfill({ json: { requestToken: token, headerName: 'X-CSRF-TOKEN' } })
      }
      if (url.pathname === '/api/v1/session/logout') {
        expect(request.method()).toBe('POST')
        expect(request.isNavigationRequest()).toBe(true)
        expect(url.searchParams.get('redirectToHome')).toBe('true')
        expect(new URLSearchParams(request.postData()!).get('__RequestVerificationToken')).toBe(token)
        authenticated = false
        return route.fulfill({ status: 302, headers: { location: endSessionUrl } })
      }
      if (url.pathname === '/api/v1/admin/setup-claim') return route.fulfill({ status: 204 })
      return route.fulfill({ json: [] })
    })

    await page.goto('/admin')
    await page.getByRole('button', { name: '退出', exact: true }).click()
    await expect(page).toHaveURL(endSessionUrl)
    await expect(page.getByRole('heading', { name: 'Signed out at the authority' })).toBeVisible()
    expect(authorityRequest).toEqual({ method: 'GET', cookie: 'test-sso=active', csrf: undefined })
    await page.goto('/admin')
    await expect(page).toHaveURL(/\/admin\/signin\?/)
  } finally {
    await new Promise<void>((resolve, reject) => authority.close(error => error ? reject(error) : resolve()))
  }
})

test('a local administrator returns to sign-in after logout', async ({ page }) => {
  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin\?/)
  await page.locator('form input[name="username"]').fill(process.env.STRUCTADOC_E2E_ADMIN_USERNAME ?? 'structadoc-admin')
  await page.locator('form input[type="password"]').fill(process.env.STRUCTADOC_E2E_ADMIN_PASSWORD ?? 'StructaDoc-E2E-Password')
  await page.locator('form button').click()
  await expect(page).toHaveURL(/\/admin$/)

  await page.getByRole('button', { name: '退出', exact: true }).click()
  await expect(page).toHaveURL(/\/signin\?/)
  await page.goto('/admin')
  await expect(page).toHaveURL(/\/admin\/signin\?/)
})
