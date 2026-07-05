export const environment = {
  production: true,
  // Empty = same origin as the Angular app. API and Angular are served
  // from the same Container App, so no cross-origin base URL is needed.
  apiUrl: '',
  b2cClientId: '#{B2C_CLIENT_ID}#',
  b2cAuthority: '#{B2C_AUTHORITY}#',
  b2cDomain: '#{B2C_DOMAIN}#',
  b2cTenantId: '#{B2C_TENANT_ID}#',
  b2cScope: '#{B2C_SCOPE}#',
};
