// Mirrors the Host's bounded OIDC outcomes (OidcSignInFlow): every sign-in failure or refusal
// arrives as one fixed `authentication` code on the sign-in route, is shown as one fixed message,
// and is then removed from the URL so a refresh or a shared link cannot replay it.
const messages: Record<string, string> = {
  denied: '登录已被取消，请重试。',
  'not-admin': '该账号没有管理员权限，请使用管理员账号登录。',
  unavailable: '暂时无法连接身份平台，请稍后重试。',
  failed: '登录失败，请重试。',
}

export function authenticationOutcome(value: unknown): string | undefined {
  return typeof value === 'string' ? messages[value] : undefined
}
