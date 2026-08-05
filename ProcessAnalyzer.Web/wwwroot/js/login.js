// The login page. Deliberately standalone: it must render before anybody is authenticated, so it shares nothing
// with the application shell beyond the stylesheet.

const form = document.getElementById('loginForm');
const error = document.getElementById('loginError');

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  error.hidden = true;

  const response = await fetch('/api/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password: document.getElementById('password').value }),
  });

  if (response.ok) {
    // Replace rather than assign: the login page should not sit in the history where Back returns to it after a
    // successful sign-in.
    window.location.replace('/');
    return;
  }

  error.textContent = 'Kennwort stimmt nicht.';
  error.hidden = false;
  document.getElementById('password').select();
});
