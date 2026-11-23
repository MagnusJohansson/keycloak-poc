// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

import { useCallback, useEffect, useState } from 'react';
import { useAuth } from 'react-oidc-context';
import type { User } from 'oidc-client-ts';
import { ApiError, callApi } from '../auth/api';
import { RequireRole, useRoles } from '../auth/guards';

type Visibility = 'tenant' | 'private';

interface Document {
  id: string;
  tenant: string;
  title: string;
  ownerUsername: string;
  visibility: Visibility;
  isClassified: boolean;
  /** Whether the API recorded you as its owner. A hint for showing "Share"; the API decides. */
  isOwner: boolean;
}

interface DocumentsResponse {
  tenant: string;
  department: string | null;
  documents: Document[];
}

type Share = { type: 'user'; id: string; username: string | null } | { type: 'department'; name: string };

interface SharesResponse {
  documentId: string;
  visibility: Visibility;
  shares: Share[];
}

function describe(e: unknown): string {
  if (!(e instanceof ApiError)) {
    return String(e);
  }
  // A problem response's title is the API's own fixed wording, safe to show.
  try {
    const problem = JSON.parse(e.message) as { title?: string; detail?: string };
    return `${e.status}: ${problem.detail ?? problem.title ?? e.message}`;
  } catch {
    return `${e.status}: ${e.message}`;
  }
}

export function DocumentsPage() {
  const auth = useAuth();
  const roles = useRoles();
  const [data, setData] = useState<DocumentsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [visibility, setVisibility] = useState<Visibility>('tenant');
  const [sharing, setSharing] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setData(await callApi<DocumentsResponse>(auth.user, '/documents'));
    } catch (e) {
      setError(describe(e));
    }
  }, [auth.user]);

  useEffect(() => {
    void load();
  }, [load]);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    try {
      await callApi(auth.user, '/documents', { method: 'POST', body: JSON.stringify({ title, visibility }) });
      setTitle('');
      await load();
    } catch (e) {
      setError(describe(e));
    }
  }

  async function remove(doc: Document) {
    try {
      await callApi(auth.user, `/documents/${doc.id}`, { method: 'DELETE' });
      await load();
    } catch (e) {
      setError(describe(e));
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
          <li key={doc.id} data-testid={`document-${doc.title}`}>
            <div className="document-row">
              <span>
                {doc.title}
                {doc.visibility === 'private' && <span className="badge">private</span>}
              </span>
              <span className="document-actions">
                <small className="muted">owned by {doc.ownerUsername}</small>
                {doc.isOwner && (
                  <>
                    <button type="button" className="secondary" onClick={() => setSharing(sharing === doc.id ? null : doc.id)}>
                      Share
                    </button>
                    <button type="button" className="secondary" onClick={() => void remove(doc)}>
                      Delete
                    </button>
                  </>
                )}
              </span>
            </div>
            {sharing === doc.id && <SharePanel user={auth.user} document={doc} onError={setError} />}
          </li>
        ))}
        {data?.documents.length === 0 && <li className="muted">No documents you can see in this tenant.</li>}
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
          <select value={visibility} onChange={(e) => setVisibility(e.target.value as Visibility)} aria-label="Visibility">
            <option value="tenant">Everyone in {data?.tenant ?? 'the tenant'}</option>
            <option value="private">Only me, and who I share with</option>
          </select>
          <button type="submit">Create</button>
        </form>
      </RequireRole>

      <p className="muted small">
        Your roles: <code>{roles.join(', ') || 'none'}</code>
      </p>
    </section>
  );
}

/**
 * Who a document is shared with, and sharing it further. Only offered to its owner, and
 * the API refuses everyone else anyway (OpenFGA's can_share).
 */
function SharePanel({ user, document, onError }: { user: User | null | undefined; document: Document; onError: (message: string) => void }) {
  const [shares, setShares] = useState<Share[] | null>(null);
  const [kind, setKind] = useState<'username' | 'department'>('username');
  const [name, setName] = useState('');

  const load = useCallback(async () => {
    try {
      setShares((await callApi<SharesResponse>(user, `/documents/${document.id}/shares`)).shares);
    } catch (e) {
      onError(describe(e));
    }
  }, [user, document.id, onError]);

  useEffect(() => {
    void load();
  }, [load]);

  async function share(event: React.FormEvent) {
    event.preventDefault();
    try {
      await callApi(user, `/documents/${document.id}/shares`, { method: 'POST', body: JSON.stringify({ [kind]: name }) });
      setName('');
      await load();
    } catch (e) {
      onError(describe(e));
    }
  }

  async function unshare(item: Share) {
    const path = item.type === 'user' ? `users/${item.id}` : `departments/${encodeURIComponent(item.name)}`;
    try {
      await callApi(user, `/documents/${document.id}/shares/${path}`, { method: 'DELETE' });
      await load();
    } catch (e) {
      onError(describe(e));
    }
  }

  return (
    <div className="share-panel" data-testid="share-panel">
      <p className="muted small">
        {document.visibility === 'tenant'
          ? `Everyone in ${document.tenant} can already see this.`
          : 'Private: only you, and the people and departments below.'}
      </p>
      <ul data-testid="shares">
        {shares?.map((item) => (
          <li key={item.type === 'user' ? item.id : item.name}>
            <span>{item.type === 'user' ? (item.username ?? 'someone who no longer exists') : `department ${item.name}`}</span>
            <button type="button" className="secondary" onClick={() => void unshare(item)}>
              Remove
            </button>
          </li>
        ))}
        {shares?.length === 0 && <li className="muted small">Not shared with anyone.</li>}
      </ul>
      <form onSubmit={share} className="new-document">
        <select value={kind} onChange={(e) => setKind(e.target.value as 'username' | 'department')} aria-label="Share with">
          <option value="username">Person</option>
          <option value="department">Department</option>
        </select>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={kind === 'username' ? 'username, e.g. carol' : 'department, e.g. legal'}
          aria-label="Share with whom"
          required
        />
        <button type="submit">Share</button>
      </form>
    </div>
  );
}
