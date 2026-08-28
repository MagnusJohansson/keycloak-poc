// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { useCallback, useEffect, useState } from 'react';
import { useAuth } from 'react-oidc-context';
import { ApiError, callApi } from '../auth/api';
import { RequireRole, useRoles } from '../auth/guards';

interface Document {
  id: string;
  tenant: string;
  title: string;
  ownerUsername: string;
  isClassified: boolean;
}

interface DocumentsResponse {
  tenant: string;
  department: string | null;
  documents: Document[];
}

export function DocumentsPage() {
  const auth = useAuth();
  const roles = useRoles();
  const [data, setData] = useState<DocumentsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [title, setTitle] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try {
      setData(await callApi<DocumentsResponse>(auth.user, '/documents'));
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    }
  }, [auth.user]);

  useEffect(() => {
    void load();
  }, [load]);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    try {
      await callApi(auth.user, '/documents', { method: 'POST', body: JSON.stringify({ title }) });
      setTitle('');
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e));
    }
  }

  return (
    <section>
      <h1>Documents</h1>
      {data && (
        <p className="muted" data-testid="tenant">
          Tenant <strong>{data.tenant}</strong>
          {data.department && <> · department <strong>{data.department}</strong></>}
        </p>
      )}
      {error && <p className="error">{error}</p>}

      <ul className="documents" data-testid="documents">
        {data?.documents.map((doc) => (
          <li key={doc.id}>
            <span>{doc.title}</span>
            <small className="muted">owned by {doc.ownerUsername}</small>
          </li>
        ))}
        {data?.documents.length === 0 && <li className="muted">No documents in this tenant.</li>}
      </ul>

      {/* The form is hidden without the role, but the API enforces it regardless -
          try POSTing directly with a doc.reader token and you still get a 403. */}
      <RequireRole anyOf={['doc.editor', 'doc.admin']}>
        <form onSubmit={create} className="new-document">
          <input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="New document title"
            required
          />
          <button type="submit">Create</button>
        </form>
      </RequireRole>

      <p className="muted small">
        Your roles: <code>{roles.join(', ') || 'none'}</code>
      </p>
    </section>
  );
}
