const status = document.getElementById('status');
const claims = document.getElementById('claims');
const list = document.getElementById('list');

async function run(action) {
  status.textContent = '';
  try {
    return await action();
  } catch (e) {
    status.textContent = String(e.message ?? e);
    return null;
  }
}

document.getElementById('signin').addEventListener('click', async () => {
  const c = await run(() => window.docvault.signIn());
  if (c) claims.textContent = JSON.stringify(c, null, 2);
});

document.getElementById('stepup').addEventListener('click', async () => {
  const c = await run(() => window.docvault.stepUp());
  if (c) claims.textContent = JSON.stringify(c, null, 2);
});

document.getElementById('signout').addEventListener('click', async () => {
  await run(() => window.docvault.signOut());
  claims.textContent = '';
  list.replaceChildren();
});

document.getElementById('documents').addEventListener('click', async () => {
  const data = await run(() => window.docvault.getDocuments());
  if (!data) return;
  if (data.error) {
    status.textContent = data.error;
    return;
  }
  list.replaceChildren(
    ...data.documents.map((doc) => {
      const li = document.createElement('li');
      li.textContent = doc.title;
      return li;
    }),
  );
});
