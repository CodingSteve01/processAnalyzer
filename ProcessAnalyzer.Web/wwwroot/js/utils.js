// The two helpers every page needs. Deliberately tiny: this app has no framework, and a "utils" file is where
// frameworks start growing when nobody watches.

/** Element by id. Throws rather than returning null, so a renamed id fails at the call site, not three frames later. */
export function $(id) {
  const element = document.getElementById(id);
  if (!element) throw new Error(`unknown element: ${id}`);
  return element;
}

/** Escapes text for insertion into HTML. Every value rendered here comes from the database, so nothing is trusted. */
export function escape(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]
  );
}
