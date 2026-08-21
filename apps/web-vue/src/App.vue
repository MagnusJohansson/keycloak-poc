<script setup lang="ts">
import { computed, ref } from 'vue';
import { useAuth, callApi } from './auth/useAuth';

const { user, isLoading, error, signIn, signOut } = useAuth();
const documents = ref<{ id: string; title: string }[]>([]);
const tenant = ref<string>('');
const apiError = ref<string | null>(null);

/** Decoded access-token payload, for the claims panel. */
const claims = computed(() => {
  const token = user.value?.access_token;
  if (!token) return null;
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(payload));
  } catch {
    return null;
  }
});

async function load() {
  apiError.value = null;
  try {
    const data = await callApi<{ tenant: string; documents: { id: string; title: string }[] }>('/documents');
    tenant.value = data.tenant;
    documents.value = data.documents;
  } catch (e) {
    apiError.value = String(e);
  }
}
</script>

<template>
  <main>
    <h1>DocVault <small>Vue</small></h1>

    <p v-if="isLoading" class="muted">Checking your session…</p>
    <p v-else-if="error" class="error">{{ error }}</p>

    <template v-else-if="!user">
      <p class="muted">Not signed in.</p>
      <button @click="signIn">Sign in</button>
    </template>

    <template v-else>
      <p class="muted">
        Signed in as <strong>{{ user.profile.preferred_username }}</strong>
        <span v-if="tenant"> · tenant <strong>{{ tenant }}</strong></span>
      </p>

      <button @click="load">Load documents</button>
      <button class="secondary" @click="signOut">Sign out</button>

      <p v-if="apiError" class="error">{{ apiError }}</p>

      <ul>
        <li v-for="doc in documents" :key="doc.id">{{ doc.title }}</li>
      </ul>

      <details>
        <summary>Access token claims</summary>
        <pre>{{ JSON.stringify(claims, null, 2) }}</pre>
      </details>
    </template>
  </main>
</template>

<style>
:root { color-scheme: dark; font-family: ui-sans-serif, system-ui, sans-serif; }
body { margin: 0; background: #0f1115; color: #e6e8ec; }
main { max-width: 720px; margin: 0 auto; padding: 2rem; }
h1 small { color: #949aa6; font-size: 0.5em; font-weight: 400; }
button { background: #5b8def; color: #fff; border: 0; border-radius: 6px; padding: .5rem .9rem; font: inherit; cursor: pointer; margin-right: .5rem; }
button.secondary { background: #272b35; }
.muted { color: #949aa6; }
.error { color: #e5534b; }
ul { list-style: none; padding: 0; display: grid; gap: .5rem; }
li { background: #171a21; border: 1px solid #272b35; border-radius: 8px; padding: .75rem 1rem; }
pre { background: #171a21; border: 1px solid #272b35; border-radius: 8px; padding: 1rem; overflow-x: auto; font-size: .8rem; }
summary { cursor: pointer; color: #949aa6; margin: 1rem 0 .5rem; }
</style>
