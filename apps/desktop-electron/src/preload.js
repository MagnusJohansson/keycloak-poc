const { contextBridge, ipcRenderer } = require('electron');

// The entire surface the renderer can reach. Note what is absent: any way to
// obtain a raw access token. The renderer receives decoded claims for display
// and data fetched on its behalf, so a script injected into the page has
// nothing to steal.
contextBridge.exposeInMainWorld('docvault', {
  signIn: () => ipcRenderer.invoke('auth:signIn'),
  signOut: () => ipcRenderer.invoke('auth:signOut'),
  stepUp: () => ipcRenderer.invoke('auth:stepUp'),
  getDocuments: () => ipcRenderer.invoke('api:documents'),
});
