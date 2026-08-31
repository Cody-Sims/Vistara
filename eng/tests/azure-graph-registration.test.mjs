import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { delimiter, dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const FIXTURES = resolve(HERE, 'fixtures/azure-graph-registration');

const MODULE_PATH = resolve(REPOSITORY_ROOT, 'deploy/azure/infra/entra/app-registration.bicep');
const BICEP_CONFIG_PATH = resolve(REPOSITORY_ROOT, 'deploy/azure/bicepconfig.json');
const BUILD_FIXTURE_PATH = resolve(FIXTURES, 'app-registration.build.json');

const MODULE_SOURCE = readFileSync(MODULE_PATH, 'utf8');
const BICEP_CONFIG = JSON.parse(readFileSync(BICEP_CONFIG_PATH, 'utf8'));
const TEMPLATE = JSON.parse(readFileSync(BUILD_FIXTURE_PATH, 'utf8'));
const GRAPH_PERMISSIONS = JSON.parse(readFileSync(resolve(FIXTURES, 'microsoft-graph-delegated-permissions.json'), 'utf8'));
const HOSTED_ROUTES = JSON.parse(readFileSync(resolve(FIXTURES, 'hosted-oidc-routes.json'), 'utf8'));

const GRAPH_EXTENSION_ALIAS = 'microsoftGraphV1';
const GRAPH_EXTENSION_REFERENCE = 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0';
const PINNED_BICEP_VERSION = '0.46.1';

// Frozen in the hosted bootstrap spec §6.4 plus the reviewed route contract.
// Changing either list is a breaking change for HB-08, HB-11, and HB-12.
const FROZEN_PARAMETERS = ['uniqueName', 'displayName', 'apiFqdn', 'apiIdentityPrincipalId', 'tenantId'];
const FROZEN_OUTPUTS = [
  'applicationClientId',
  'applicationObjectId',
  'servicePrincipalObjectId',
  'applicationUniqueName',
  'redirectUri',
  'frontChannelLogoutUri',
  'postLogoutRedirectUri',
  'federatedCredentialName',
  'federatedCredentialIssuer',
  'federatedCredentialSubject',
  'federatedCredentialAudience',
];

// https://learn.microsoft.com/graph/templates/bicep/concept-uniquely-named-resources
const GRAPH_UNIQUE_NAME_MAX_LENGTH = 120;
// `uniqueString()` is documented to return a 13-character base-32 hash.
const ARM_UNIQUE_STRING_LENGTH = 13;
// `azd` rejects environment names longer than this, so it bounds the base name.
const AZD_ENVIRONMENT_NAME_MAX_LENGTH = 64;
const UNIQUE_NAME_PREFIX = 'vistara-';

// ---------------------------------------------------------------------------
// A deliberately small ARM template-expression evaluator. It resolves the
// module's own composition (normalization, route joining, alternate-key
// derivation) without reimplementing anything Azure owns: `uniqueString` is
// modelled as an opaque, deterministic marker over its arguments, which is the
// only property this module relies on.
// ---------------------------------------------------------------------------

function tokenize(expression) {
  const tokens = [];
  let index = 0;

  while (index < expression.length) {
    const character = expression[index];

    if (/\s/.test(character)) {
      index += 1;
      continue;
    }

    if (character === "'") {
      let value = '';
      index += 1;
      while (index < expression.length) {
        if (expression[index] === "'") {
          if (expression[index + 1] === "'") {
            value += "'";
            index += 2;
            continue;
          }
          index += 1;
          break;
        }
        value += expression[index];
        index += 1;
      }
      tokens.push({ kind: 'string', value });
      continue;
    }

    if ('(),.[]'.includes(character)) {
      tokens.push({ kind: character });
      index += 1;
      continue;
    }

    const identifier = /^[A-Za-z0-9_$-]+/.exec(expression.slice(index));
    assert.ok(identifier, `unexpected character '${character}' in expression`);
    tokens.push({ kind: 'identifier', value: identifier[0] });
    index += identifier[0].length;
  }

  return tokens;
}

function evaluateExpression(expression, context) {
  if (typeof expression !== 'string' || !expression.startsWith('[') || !expression.endsWith(']')) {
    return expression;
  }

  const tokens = tokenize(expression.slice(1, -1));
  let cursor = 0;

  const peek = () => tokens[cursor];
  const take = (kind) => {
    const token = tokens[cursor];
    assert.ok(token && (!kind || token.kind === kind), `expected ${kind} at token ${cursor} of ${expression}`);
    cursor += 1;
    return token;
  };

  function parseValue() {
    const token = take();

    let value;
    if (token.kind === 'string') {
      value = token.value;
    } else {
      assert.equal(token.kind, 'identifier', `unsupported token in ${expression}`);
      if (peek()?.kind === '(') {
        take('(');
        const args = [];
        while (peek()?.kind !== ')') {
          args.push(parseValue());
          if (peek()?.kind === ',') {
            take(',');
          }
        }
        take(')');
        value = callFunction(token.value, args);
      } else if (/^-?\d+$/.test(token.value)) {
        value = Number(token.value);
      } else {
        assert.fail(`bare identifier '${token.value}' is not supported in ${expression}`);
      }
    }

    while (peek()?.kind === '.' || peek()?.kind === '[') {
      if (peek().kind === '.') {
        take('.');
        const property = take('identifier').value;
        assert.ok(value !== null && typeof value === 'object', `cannot read '${property}' in ${expression}`);
        assert.ok(property in value, `unknown property '${property}' in ${expression}`);
        value = value[property];
      } else {
        take('[');
        const key = parseValue();
        take(']');
        value = value[key];
      }
    }

    return value;
  }

  function callFunction(name, args) {
    switch (name) {
      case 'parameters':
        assert.ok(args[0] in context.parameters, `unbound parameter '${args[0]}'`);
        return context.parameters[args[0]];
      case 'variables':
        return evaluateVariable(args[0], context);
      case 'format':
        return String(args[0]).replace(/\{(\d+)\}/g, (_, position) => String(args[Number(position) + 1]));
      case 'concat':
        return args.join('');
      case 'toLower':
        return String(args[0]).toLowerCase();
      case 'toUpper':
        return String(args[0]).toUpperCase();
      case 'trim':
        return String(args[0]).trim();
      case 'substring':
        return String(args[0]).substring(args[1], args[1] + args[2]);
      case 'subscription':
        return context.subscription;
      case 'resourceGroup':
        return context.resourceGroup;
      case 'tenant':
        return context.tenant;
      // Modelled, not reimplemented: ARM guarantees only that the result is a
      // deterministic function of the argument list.
      case 'uniqueString':
        return `«hash:${args.join('\u0000')}»`;
      default:
        return assert.fail(`unsupported ARM function '${name}' in ${expression}`);
    }
  }

  const result = parseValue();
  assert.equal(cursor, tokens.length, `trailing tokens in ${expression}`);
  return result;
}

function evaluateVariable(name, context) {
  assert.ok(name in TEMPLATE.variables, `unknown variable '${name}'`);
  return evaluateExpression(TEMPLATE.variables[name], context);
}

function scope({
  tenantId = '11111111-1111-1111-1111-111111111111',
  subscriptionId = '22222222-2222-2222-2222-222222222222',
  resourceGroupName = 'rg-vistara-eval',
  uniqueName = 'vistara-eval',
  displayName = 'Vistara',
  apiFqdn = 'api.example.invalid',
  apiIdentityPrincipalId = '33333333-3333-3333-3333-333333333333',
} = {}) {
  return {
    parameters: { uniqueName, displayName, apiFqdn, apiIdentityPrincipalId, tenantId },
    subscription: { subscriptionId, tenantId },
    resourceGroup: {
      id: `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroupName}`,
      name: resourceGroupName,
    },
    tenant: { tenantId },
  };
}

const DEFAULT_SCOPE = scope();

function resourceOf(symbol) {
  assert.ok(symbol in TEMPLATE.resources, `expected resource '${symbol}'`);
  return TEMPLATE.resources[symbol];
}

function walkStrings(node, visit, path = '$') {
  if (typeof node === 'string') {
    visit(node, path);
    return;
  }
  if (Array.isArray(node)) {
    node.forEach((entry, position) => walkStrings(entry, visit, `${path}[${position}]`));
    return;
  }
  if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node)) {
      visit(key, `${path}.${key} (key)`);
      walkStrings(value, visit, `${path}.${key}`);
    }
  }
}

function resolveBicepCli() {
  const explicit = process.env.VISTARA_BICEP_CLI;
  if (explicit && existsSync(explicit)) {
    return explicit;
  }
  for (const directory of (process.env.PATH ?? '').split(delimiter)) {
    if (directory && existsSync(join(directory, 'bicep'))) {
      return join(directory, 'bicep');
    }
  }
  return null;
}

const BICEP_CLI = resolveBicepCli();
const BICEP_SKIP = 'no Bicep CLI found; set VISTARA_BICEP_CLI (see eng/tests/fixtures/azure-graph-registration/README.md)';

function withoutGenerator(template) {
  const { metadata, ...rest } = template;
  const { _generator, ...remainingMetadata } = metadata ?? {};
  return Object.keys(remainingMetadata).length > 0 ? { ...rest, metadata: remainingMetadata } : rest;
}

// ---------------------------------------------------------------------------
// Bicep CLI: build, lint, and fixture drift.
// ---------------------------------------------------------------------------

test('the Bicep CLI builds the module and the committed template matches', (t) => {
  if (!BICEP_CLI) {
    t.skip(BICEP_SKIP);
    return;
  }

  const version = spawnSync(BICEP_CLI, ['--version'], { encoding: 'utf8' });
  assert.equal(version.status, 0, version.stderr);
  const parsed = /(\d+)\.(\d+)\.(\d+)/.exec(version.stdout);
  assert.ok(parsed, `could not read a version from '${version.stdout.trim()}'`);

  const [major, minor, patch] = parsed.slice(1, 4).map(Number);
  const [pinnedMajor, pinnedMinor, pinnedPatch] = PINNED_BICEP_VERSION.split('.').map(Number);
  const ordered = major * 1e6 + minor * 1e3 + patch;
  const pinned = pinnedMajor * 1e6 + pinnedMinor * 1e3 + pinnedPatch;
  assert.ok(ordered >= pinned, `Bicep ${PINNED_BICEP_VERSION} or newer is required, found ${parsed[0]}`);

  const build = spawnSync(BICEP_CLI, ['build', '--stdout', MODULE_PATH], { encoding: 'utf8', cwd: REPOSITORY_ROOT });
  assert.equal(build.status, 0, `bicep build failed:\n${build.stderr}`);
  assert.equal(build.stderr.trim(), '', `bicep build emitted diagnostics:\n${build.stderr}`);

  assert.deepEqual(
    withoutGenerator(JSON.parse(build.stdout)),
    withoutGenerator(TEMPLATE),
    'app-registration.build.json is stale; regenerate it (see the fixture README)',
  );
});

test('the Bicep CLI lints the module with no diagnostics', (t) => {
  if (!BICEP_CLI) {
    t.skip(BICEP_SKIP);
    return;
  }

  const lint = spawnSync(BICEP_CLI, ['lint', MODULE_PATH], { encoding: 'utf8', cwd: REPOSITORY_ROOT });
  assert.equal(lint.status, 0, `bicep lint failed:\n${lint.stdout}${lint.stderr}`);
  assert.equal(`${lint.stdout}${lint.stderr}`.trim(), '', 'bicep lint must report nothing');
});

test('the committed template was produced by the pinned Bicep version', () => {
  assert.equal(TEMPLATE.languageVersion, '2.0');
  assert.ok(
    TEMPLATE.metadata._generator.version.startsWith(`${PINNED_BICEP_VERSION}.`),
    `expected a ${PINNED_BICEP_VERSION} build, found ${TEMPLATE.metadata._generator.version}`,
  );
});

// ---------------------------------------------------------------------------
// Extension and type surface.
// ---------------------------------------------------------------------------

test('the Graph extension is pinned to the reviewed v1.0.0 release', () => {
  assert.deepEqual(BICEP_CONFIG.extensions, { [GRAPH_EXTENSION_ALIAS]: GRAPH_EXTENSION_REFERENCE });
  assert.match(MODULE_SOURCE, new RegExp(`^extension ${GRAPH_EXTENSION_ALIAS}$`, 'm'));
  assert.deepEqual(TEMPLATE.imports, {
    [GRAPH_EXTENSION_ALIAS]: { provider: 'MicrosoftGraph', version: '1.0.0' },
  });
});

test('only Microsoft Graph resource types are declared, all bound to the pinned extension', () => {
  assert.deepEqual(Object.keys(TEMPLATE.resources), ['application', 'apiManagedIdentityCredential', 'servicePrincipal']);
  assert.deepEqual(
    Object.values(TEMPLATE.resources).map((entry) => entry.type),
    [
      'Microsoft.Graph/applications@v1.0',
      'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0',
      'Microsoft.Graph/servicePrincipals@v1.0',
    ],
  );
  for (const [symbol, entry] of Object.entries(TEMPLATE.resources)) {
    assert.equal(entry.import, GRAPH_EXTENSION_ALIAS, `${symbol} must bind to the pinned extension`);
    assert.ok(!('existing' in entry), `${symbol} must not adopt an unmanaged object`);
  }
});

test('the module exposes exactly the frozen parameter interface', () => {
  assert.deepEqual(Object.keys(TEMPLATE.parameters), FROZEN_PARAMETERS);
  for (const [name, parameter] of Object.entries(TEMPLATE.parameters)) {
    assert.equal(parameter.type, 'string', `${name} must stay a string`);
    assert.ok(!('defaultValue' in parameter), `${name} must not carry a default`);
    assert.ok(Number.isInteger(parameter.minLength), `${name} must declare @minLength`);
    assert.ok(Number.isInteger(parameter.maxLength), `${name} must declare @maxLength`);
    assert.ok(parameter.metadata?.description, `${name} must be described`);
  }

  // GUID-shaped inputs are pinned to the canonical 36-character form.
  for (const name of ['apiIdentityPrincipalId', 'tenantId']) {
    assert.equal(TEMPLATE.parameters[name].minLength, 36);
    assert.equal(TEMPLATE.parameters[name].maxLength, 36);
  }
});

test('the module contracts identifiers and reply URLs, never a credential', () => {
  assert.deepEqual(Object.keys(TEMPLATE.outputs), FROZEN_OUTPUTS);
  for (const [name, output] of Object.entries(TEMPLATE.outputs)) {
    assert.equal(output.type, 'string', `${name} must stay a string`);
    assert.ok(output.metadata?.description, `${name} must be described`);
  }

  assert.deepEqual(
    Object.entries(TEMPLATE.outputs).map(([name, output]) => [name, output.value]),
    [
      ['applicationClientId', "[reference('application').appId]"],
      ['applicationObjectId', "[reference('application').id]"],
      ['servicePrincipalObjectId', "[reference('servicePrincipal').id]"],
      ['applicationUniqueName', "[variables('effectiveUniqueName')]"],
      ['redirectUri', "[variables('callbackUri')]"],
      ['frontChannelLogoutUri', "[variables('frontChannelLogoutUri')]"],
      ['postLogoutRedirectUri', "[variables('signedOutUri')]"],
      ['federatedCredentialName', "[variables('federatedCredentialName')]"],
      ['federatedCredentialIssuer', "[variables('federatedCredentialIssuerValue')]"],
      ['federatedCredentialSubject', "[variables('normalizedApiIdentityPrincipalId')]"],
      ['federatedCredentialAudience', "[variables('federatedCredentialAudienceValue')]"],
    ],
  );
});

// ---------------------------------------------------------------------------
// Frozen hosted routes.
// ---------------------------------------------------------------------------

test('the three hosted routes match the frozen route contract', () => {
  assert.equal(evaluateVariable('callbackRoute', DEFAULT_SCOPE), HOSTED_ROUTES.routes.callback.path);
  assert.equal(evaluateVariable('frontChannelLogoutRoute', DEFAULT_SCOPE), HOSTED_ROUTES.routes.frontChannelLogout.path);
  assert.equal(evaluateVariable('signedOutRoute', DEFAULT_SCOPE), HOSTED_ROUTES.routes.signedOut.path);

  assert.equal(HOSTED_ROUTES.routes.callback.path, '/api/v1/auth/oidc/entra/callback');
  assert.equal(HOSTED_ROUTES.routes.frontChannelLogout.path, '/api/v1/auth/oidc/entra/frontchannel-logout');
  assert.equal(HOSTED_ROUTES.routes.signedOut.path, '/api/v1/auth/oidc/entra/signed-out');

  for (const route of Object.values(HOSTED_ROUTES.routes)) {
    assert.equal(route.method, 'GET');
    assert.equal(route.anonymous, true);
  }
});

test('the callback and signed-out routes are the reply URLs and front-channel logout is the sign-out URL', () => {
  const web = resourceOf('application').properties.web;

  assert.deepEqual(web.redirectUris, ["[variables('callbackUri')]", "[variables('signedOutUri')]"]);
  assert.equal(web.logoutUrl, "[variables('frontChannelLogoutUri')]");
  assert.equal(web.homePageUrl, "[variables('apiBaseUri')]");

  assert.equal(HOSTED_ROUTES.routes.callback.entraRegistration, 'web.redirectUris');
  assert.equal(HOSTED_ROUTES.routes.signedOut.entraRegistration, 'web.redirectUris');
  assert.equal(HOSTED_ROUTES.routes.frontChannelLogout.entraRegistration, 'web.logoutUrl');

  // A web-only confidential client: no other platform may hold a reply URL.
  assert.deepEqual(resourceOf('application').properties.publicClient, { redirectUris: [] });
  assert.deepEqual(resourceOf('application').properties.spa, { redirectUris: [] });
});

test('the module owns the whole reply-URL list and every entry is an exact HTTPS route on the API host', () => {
  const context = scope({ apiFqdn: 'api.example.invalid' });
  const web = resourceOf('application').properties.web;
  const registered = web.redirectUris.map((uri) => evaluateExpression(uri, context));

  assert.deepEqual(registered, [
    'https://api.example.invalid/api/v1/auth/oidc/entra/callback',
    'https://api.example.invalid/api/v1/auth/oidc/entra/signed-out',
  ]);
  assert.equal(
    evaluateExpression(web.logoutUrl, context),
    'https://api.example.invalid/api/v1/auth/oidc/entra/frontchannel-logout',
  );

  // The declarative list replaces whatever Entra holds, so nothing outside the
  // module's own derivation may appear in it and no entry may be a wildcard.
  for (const uri of [...registered, evaluateExpression(web.logoutUrl, context)]) {
    assert.ok(uri.startsWith('https://api.example.invalid/'), `${uri} must be on the API host`);
    assert.ok(!uri.includes('*'), `${uri} must not contain a wildcard`);
    assert.ok(!uri.includes('http://'), `${uri} must not fall back to plaintext HTTP`);
  }
  assert.equal(new Set(registered).size, registered.length, 'reply URLs must be distinct');
});

test('reply URLs survive mixed case and stray whitespace on the API host', () => {
  const context = scope({ apiFqdn: '  API.Example.INVALID\n' });
  const web = resourceOf('application').properties.web;

  assert.deepEqual(
    web.redirectUris.map((uri) => evaluateExpression(uri, context)),
    [
      'https://api.example.invalid/api/v1/auth/oidc/entra/callback',
      'https://api.example.invalid/api/v1/auth/oidc/entra/signed-out',
    ],
  );
});

// ---------------------------------------------------------------------------
// Single tenant, claims, and requested permissions.
// ---------------------------------------------------------------------------

test('the application is single tenant', () => {
  assert.equal(resourceOf('application').properties.signInAudience, 'AzureADMyOrg');
  for (const rejected of ['AzureADMultipleOrgs', 'AzureADandPersonalMicrosoftAccount', 'PersonalMicrosoftAccount']) {
    assert.ok(!JSON.stringify(TEMPLATE).includes(rejected), `multi-tenant audience '${rejected}' must not appear`);
  }
});

test('no id token, access token, or groups claim is requested implicitly', () => {
  const properties = resourceOf('application').properties;
  assert.deepEqual(properties.web.implicitGrantSettings, {
    enableAccessTokenIssuance: false,
    enableIdTokenIssuance: false,
  });
  assert.equal(properties.groupMembershipClaims, 'None');
  assert.deepEqual(properties.optionalClaims, { accessToken: [], idToken: [], saml2Token: [] });
});

test('requested permissions match the cited Microsoft Graph delegated permission IDs', () => {
  const context = DEFAULT_SCOPE;
  const requested = resourceOf('application').properties.requiredResourceAccess;

  assert.equal(requested.length, 1, 'only Microsoft Graph may be requested');
  assert.equal(evaluateExpression(requested[0].resourceAppId, context), GRAPH_PERMISSIONS.resourceAppId);

  const scopes = GRAPH_PERMISSIONS.delegatedPermissions;
  assert.deepEqual(Object.keys(scopes), ['openid', 'profile', 'email']);
  assert.deepEqual(
    requested[0].resourceAccess.map((entry) => ({
      id: evaluateExpression(entry.id, context),
      type: entry.type,
    })),
    Object.values(scopes).map((permission) => ({ id: permission.id, type: 'Scope' })),
  );

  // The fixture is the source of truth, so guard it against a silent edit.
  assert.equal(GRAPH_PERMISSIONS.resourceAppId, '00000003-0000-0000-c000-000000000000');
  assert.equal(scopes.openid.id, '37f7f235-527c-4136-accd-4a02d197296e');
  assert.equal(scopes.profile.id, '14dad69e-099b-42c9-810b-d002981feec1');
  assert.equal(scopes.email.id, '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0');
  assert.ok(GRAPH_PERMISSIONS.sources.length >= 2, 'the fixture must cite where its IDs come from');
  for (const permission of Object.values(scopes)) {
    assert.match(permission.id, /^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$/);
    assert.equal(permission.adminConsentRequired, false);
  }
});

// ---------------------------------------------------------------------------
// Secretless.
// ---------------------------------------------------------------------------

test('the application declares no secret credential of any kind', () => {
  const properties = resourceOf('application').properties;
  assert.deepEqual(properties.passwordCredentials, []);
  assert.deepEqual(properties.keyCredentials, []);
  assert.equal(properties.isFallbackPublicClient, false);
  assert.equal(properties.isDeviceOnlyAuthSupported, false);
  assert.ok(!('tokenEncryptionKeyId' in properties));
});

test('no secret can enter or leave the template', () => {
  for (const parameter of Object.values(TEMPLATE.parameters)) {
    assert.notEqual(parameter.type, 'secureString');
    assert.notEqual(parameter.type, 'secureObject');
  }

  const forbidden = /listKeys|listSecrets|getSecret|Microsoft\.KeyVault|clientSecret|passwordCredential\s*\(/i;
  walkStrings(TEMPLATE, (value, path) => {
    assert.doesNotMatch(value, forbidden, `secret-shaped content at ${path}: ${value}`);
  });

  for (const name of Object.keys(TEMPLATE.outputs)) {
    assert.doesNotMatch(name, /secret|password|assertion|thumbprint|privatekey/i);
  }
});

// ---------------------------------------------------------------------------
// Federated identity credential.
// ---------------------------------------------------------------------------

test('the federated identity credential carries the exact trusted issuer, subject, and audience', () => {
  const context = scope({
    tenantId: '  AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE ',
    apiIdentityPrincipalId: ' 44444444-5555-6666-7777-888888888888\t',
  });
  const credential = resourceOf('apiManagedIdentityCredential');
  const expected = HOSTED_ROUTES.federatedIdentityCredential;

  assert.equal(evaluateVariable('federatedCredentialName', context), expected.name);
  assert.equal(
    evaluateExpression(credential.properties.issuer, context),
    expected.issuerTemplate.replace('{tenantId}', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
  );
  assert.equal(evaluateExpression(credential.properties.subject, context), '44444444-5555-6666-7777-888888888888');
  assert.deepEqual(credential.properties.audiences.map((audience) => evaluateExpression(audience, context)), expected.audiences);
  assert.deepEqual(expected.audiences, ['api://AzureADTokenExchange']);

  // Child of the application through the Graph alternate key path.
  assert.equal(credential.properties.name, "[format('{0}/{1}', reference('application').uniqueName, variables('federatedCredentialName'))]");
  assert.deepEqual(credential.dependsOn, ['application']);

  // A bad issuer, subject, or audience deploys clean and fails only at token
  // exchange, so none of the three may carry stray whitespace or drift.
  const issuer = evaluateExpression(credential.properties.issuer, context);
  assert.equal(issuer, issuer.trim());
  assert.ok(issuer.startsWith('https://login.microsoftonline.com/'));
  assert.ok(issuer.endsWith('/v2.0'));
});

test('the service principal is created from the application and exposes only its object ID', () => {
  assert.deepEqual(resourceOf('servicePrincipal').properties, { appId: "[reference('application').appId]" });
  assert.deepEqual(resourceOf('servicePrincipal').dependsOn, ['application']);
});

// ---------------------------------------------------------------------------
// Idempotent, collision-safe alternate key.
// ---------------------------------------------------------------------------

test('reruns of the same environment in the same scope upsert one application', () => {
  const first = evaluateVariable('effectiveUniqueName', scope());
  const second = evaluateVariable('effectiveUniqueName', scope());

  assert.equal(first, second);
  assert.equal(resourceOf('application').properties.uniqueName, "[variables('effectiveUniqueName')]");
  assert.ok(first.startsWith('vistara-eval-'), `expected the environment base to lead, got ${first}`);
});

test('the alternate key is unique per tenant, subscription, and resource group', () => {
  const baseline = evaluateVariable('effectiveUniqueName', scope());
  const variants = {
    'a different subscription': scope({ subscriptionId: '99999999-9999-9999-9999-999999999999' }),
    'a different resource group': scope({ resourceGroupName: 'rg-vistara-eval-2' }),
    'a different tenant': scope({ tenantId: '77777777-7777-7777-7777-777777777777' }),
    'a different environment': scope({ uniqueName: 'vistara-staging' }),
  };

  for (const [label, variant] of Object.entries(variants)) {
    assert.notEqual(evaluateVariable('effectiveUniqueName', variant), baseline, `${label} must not collide`);
  }

  // Two environments that share a name in different scopes must stay distinct.
  const collidingA = scope({ subscriptionId: '22222222-2222-2222-2222-222222222222', resourceGroupName: 'rg-a' });
  const collidingB = scope({ subscriptionId: '22222222-2222-2222-2222-222222222222', resourceGroupName: 'rg-b' });
  assert.notEqual(evaluateVariable('effectiveUniqueName', collidingA), evaluateVariable('effectiveUniqueName', collidingB));
});

test('the alternate key is deterministic and stays inside the Graph limit', () => {
  assert.equal(
    TEMPLATE.variables.scopeDiscriminator,
    "[uniqueString(variables('normalizedTenantId'), subscription().subscriptionId, resourceGroup().id)]",
  );

  const serialized = JSON.stringify(TEMPLATE);
  for (const nonDeterministic of ['newGuid(', 'utcNow(', 'dateTimeAdd(', 'guid(']) {
    assert.ok(!serialized.includes(nonDeterministic), `${nonDeterministic} makes redeployment non-idempotent`);
  }

  const baseMaxLength = TEMPLATE.parameters.uniqueName.maxLength;
  const longest = baseMaxLength + '-'.length + ARM_UNIQUE_STRING_LENGTH;
  assert.ok(
    longest <= GRAPH_UNIQUE_NAME_MAX_LENGTH,
    `the longest alternate key is ${longest}, over the Graph limit of ${GRAPH_UNIQUE_NAME_MAX_LENGTH}`,
  );

  const longestBase = UNIQUE_NAME_PREFIX + 'e'.repeat(AZD_ENVIRONMENT_NAME_MAX_LENGTH);
  assert.ok(
    longestBase.length <= baseMaxLength,
    `vistara-<environmentName> must fit the base: ${longestBase.length} > ${baseMaxLength}`,
  );
});

test('every input is whitespace and case normalized before it reaches Graph', () => {
  assert.deepEqual(
    Object.fromEntries(
      ['normalizedUniqueName', 'normalizedDisplayName', 'normalizedApiFqdn', 'normalizedApiIdentityPrincipalId', 'normalizedTenantId']
        .map((name) => [name, TEMPLATE.variables[name]]),
    ),
    {
      normalizedUniqueName: "[toLower(trim(parameters('uniqueName')))]",
      normalizedApiFqdn: "[toLower(trim(parameters('apiFqdn')))]",
      normalizedApiIdentityPrincipalId: "[toLower(trim(parameters('apiIdentityPrincipalId')))]",
      normalizedTenantId: "[toLower(trim(parameters('tenantId')))]",
      // Display names are shown to humans, so casing is preserved.
      normalizedDisplayName: "[trim(parameters('displayName'))]",
    },
  );

  // No raw parameter may reach a resource, an output, or a derived variable.
  const normalizing = new Set(Object.values(TEMPLATE.variables).filter((value) => typeof value === 'string'
    && /^\[(toLower\()?trim\(parameters\('[A-Za-z]+'\)\)\)?\]$/.test(value)));
  const everythingElse = JSON.stringify({
    variables: Object.fromEntries(Object.entries(TEMPLATE.variables).filter(([, value]) => !normalizing.has(value))),
    resources: TEMPLATE.resources,
    outputs: TEMPLATE.outputs,
  });

  for (const parameter of FROZEN_PARAMETERS) {
    assert.ok(
      !everythingElse.includes(`parameters('${parameter}')`),
      `${parameter} is consumed without normalization`,
    );
  }

  assert.equal(evaluateVariable('normalizedDisplayName', scope({ displayName: '  Vistara Eval  ' })), 'Vistara Eval');
});

// ---------------------------------------------------------------------------
// Operator documentation.
// ---------------------------------------------------------------------------

test('the module documents the --skip-app-registration fallback that HB-12 depends on', () => {
  const header = MODULE_SOURCE.slice(0, MODULE_SOURCE.indexOf('targetScope'));
  for (const expected of [
    '--skip-app-registration',
    'deployAppRegistration',
    'existingApplicationClientId',
    'az ad app create',
    'az ad app federated-credential create',
    'postprovision-verify-fic.sh',
    HOSTED_ROUTES.routes.callback.path,
    HOSTED_ROUTES.routes.frontChannelLogout.path,
    HOSTED_ROUTES.routes.signedOut.path,
  ]) {
    assert.ok(header.includes(expected), `the module header must document '${expected}'`);
  }
});

test('the module documents why replacing the reply-URL list is safe', () => {
  const header = MODULE_SOURCE.slice(0, MODULE_SOURCE.indexOf('targetScope'));
  assert.match(header, /Redirect-list ownership/);
  assert.match(header, /scope-discriminated/);
});
