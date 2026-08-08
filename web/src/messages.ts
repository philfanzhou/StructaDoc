import { ref } from 'vue'

export const error = ref('')
export const notice = ref('')

export function message(value: string, failure = false) {
  if (failure) error.value = value; else notice.value = value
  window.setTimeout(() => failure ? error.value = '' : notice.value = '', 5000)
}
