// A public, owner-approved alias only. Never configure the private forwarding inbox here.
export function privacyContactEmail(value: string | undefined): string | undefined {
  if (!value || value.length > 254 || value !== value.trim()) return undefined;
  // Deliberately support simple mailboxes, not display names, headers or mailto query strings.
  const mailbox = /^[a-z0-9]+(?:[._+-][a-z0-9]+)*@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;
  if (!mailbox.test(value) || value.indexOf("@") > 64) return undefined;
  return value;
}
