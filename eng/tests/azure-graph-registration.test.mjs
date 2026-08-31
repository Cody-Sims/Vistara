import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const AZURE_DIRECTORY = resolve(REPOSITORY_ROOT, 'deploy/azure');
const BICEP_CONFIG_PATH = resolve(AZURE_DIRECTORY, 'bicepconfig.json');
const MODULE_PATH = resolve(AZURE_DIRECTORY, 'infra/entra/app-registration.bicep');

// Frozen in the hosted bootstrap spec §6.4. Changing either shape is a breaking
// change for `infra/main.bicep` (HB-08) and the `up.sh` hooks (HB-12).
const FROZEN_PARAMETERS = ['uniqueName', 'displayName', 'apiFqdn', 'apiIdentityPrincipalId', 'tenantId'];
const FROZEN_OUTPUTS = ['applicationClientId', 'applicationObjectId', 'servicePrincipalObjectId'];

const GRAPH_EXTENSION_ALIAS = 'microsoftGraphV1';
const GRAPH_EXTENSION_REFERENCE = 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0';

// https://learn.microsoft.com/graph/templates/bicep/concept-uniquely-named-resources
const UNIQUE_NAME_MAX_LENGTH = 120;
// `azd` rejects environment names longer than this, so it bounds the derived name.
const AZD_ENVIRONMENT_NAME_MAX_LENGTH = 64;
const UNIQUE_NAME_PREFIX = 'vistara-';

const RAW_MODULE = readFileSync(MODULE_PATH, 'utf8');
const BICEP_CONFIG = JSON.parse(readFileSync(BICEP_CONFIG_PATH, 'utf8'));

/**
 * Removes `//` comments without touching single-quoted string content, so that
 * URIs such as `https://example.invalid` survive and commentary does not leak
 * into the structural assertions below.
 */
function stripComments(source) {
  let output = '';
  let index = 0;
  let inString = false;

  while (index < source.length) {
    const character = source[index];

    if (inString) {
      if (character === '\\') {
        output += source.slice(index, index + 2);
        index += 2;
        continue;
      }
      if (character === "'") {
        inString = false;
      }
      output += character;
      index += 1;
      continue;
    }

    if (character === "'") {
      inString = true;
      output += character;
      index += 1;
      continue;
    }

    if (character === '/' && source[index + 1] === '/') {
      while (index < source.length && source[index] !== '\n') {
        index += 1;
      }
      continue;
    }

    output += character;
    index += 1;
  }

  return output;
}

const MODULE = stripComments(RAW_MODULE);

function collapse(text) {
  return text.replace(/\s+/g, ' ').trim();
}

function stringLiterals(source) {
  return [...source.matchAll(/'((?:[^'\\\n]|\\.)*)'/g)].map((match) => match[1]);
}

function declarations(source, keyword) {
  const found = [];
  let decorators = [];

  for (const line of source.split('\n')) {
    const trimmed = line.trim();
    if (trimmed.startsWith('@')) {
      decorators.push(trimmed);
      continue;
    }
    if (trimmed === '') {
      continue;
    }

    const match = new RegExp(`^${keyword}\\s+([A-Za-z_][A-Za-z0-9_]*)\\s+([A-Za-z0-9_\\[\\]]+)\\s*(?:=\\s*(.*))?$`).exec(trimmed);
    if (match) {
      found.push({ name: match[1], type: match[2], value: (match[3] ?? '').trim(), decorators });
    }
    decorators = [];
  }

  return found;
}

const PARAMETERS = declarations(MODULE, 'param');
const OUTPUTS = declarations(MODULE, 'output');

function variableValue(name) {
  const match = new RegExp(`^var\\s+${name}\\s*=\\s*(.+)$`, 'm').exec(MODULE);
  assert.ok(match, `expected the module to declare 'var ${name}'`);
  return match[1].trim();
}

/** Returns the text between the balanced `open`/`close` pair starting at `start`. */
function matchBalanced(source, start, open, close) {
  let depth = 0;
  let inString = false;

  for (let index = start; index < source.length; index += 1) {
    const character = source[index];

    if (inString) {
      if (character === '\\') {
        index += 1;
      } else if (character === "'") {
        inString = false;
      }
      continue;
    }

    if (character === "'") {
      inString = true;
      continue;
    }
    if (character === open) {
      depth += 1;
    } else if (character === close) {
      depth -= 1;
      if (depth === 0) {
        return source.slice(start, index + 1);
      }
    }
  }

  throw new Error(`unbalanced '${open}' starting at offset ${start}`);
}

function resourceBlock(symbol) {
  const match = new RegExp(`^resource\\s+${symbol}\\s+'([^']+)'\\s*=\\s*\\{`, 'm').exec(MODULE);
  assert.ok(match, `expected the module to declare 'resource ${symbol}'`);
  const bodyStart = MODULE.indexOf('{', match.index);
  return { type: match[1], body: matchBalanced(MODULE, bodyStart, '{', '}') };
}

/** Reads a property value out of an object literal, brace/bracket matched. */
function property(body, name) {
  const match = new RegExp(`(^|\\n)\\s*${name}\\s*:\\s*`).exec(body);
  assert.ok(match, `expected property '${name}'`);
  const valueStart = match.index + match[0].length;
  const first = body[valueStart];

  if (first === '{') {
    return matchBalanced(body, valueStart, '{', '}');
  }
  if (first === '[') {
    return matchBalanced(body, valueStart, '[', ']');
  }
  return body.slice(valueStart, body.indexOf('\n', valueStart)).trim();
}

function hasProperty(body, name) {
  return new RegExp(`(^|\\n)\\s*${name}\\s*:\\s*`).test(body);
}

const APPLICATION = resourceBlock('application');
const FEDERATED_CREDENTIAL = resourceBlock('apiManagedIdentityCredential');
const SERVICE_PRINCIPAL = resourceBlock('servicePrincipal');

test('the Graph extension is pinned to the reviewed v1.0.0 release', () => {
  assert.deepEqual(BICEP_CONFIG.extensions, { [GRAPH_EXTENSION_ALIAS]: GRAPH_EXTENSION_REFERENCE });
  assert.match(RAW_MODULE, new RegExp(`^extension ${GRAPH_EXTENSION_ALIAS}$`, 'm'));
});

test('the module exposes exactly the frozen parameter interface', () => {
  assert.deepEqual(PARAMETERS.map((parameter) => parameter.name), FROZEN_PARAMETERS);
  for (const parameter of PARAMETERS) {
    assert.equal(parameter.type, 'string', `${parameter.name} must stay a string`);
    assert.equal(parameter.value, '', `${parameter.name} must not carry a default`);
    assert.ok(
      parameter.decorators.some((decorator) => decorator.startsWith('@description(')),
      `${parameter.name} must be described`,
    );
  }
});

test('the module returns identifiers only, never a credential', () => {
  assert.deepEqual(OUTPUTS.map((output) => output.name), FROZEN_OUTPUTS);
  for (const output of OUTPUTS) {
    assert.equal(output.type, 'string');
    assert.doesNotMatch(output.name, /secret|password|credential|key|assertion|thumbprint/i);
    assert.doesNotMatch(output.value, /secret|password|passwordCredential|listKeys|listSecrets/i);
  }
  assert.deepEqual(
    OUTPUTS.map((output) => output.value),
    ['application.appId', 'application.id', 'servicePrincipal.id'],
  );
});

test('the application is single tenant', () => {
  assert.equal(property(APPLICATION.body, 'signInAudience'), "'AzureADMyOrg'");
  for (const rejected of ['AzureADMultipleOrgs', 'AzureADandPersonalMicrosoftAccount', 'PersonalMicrosoftAccount']) {
    assert.ok(!MODULE.includes(rejected), `multi-tenant audience '${rejected}' must not appear`);
  }
});

test('the reply and sign-out URIs are exact HTTPS values with no wildcard', () => {
  assert.equal(variableValue('apiBaseUri'), "'https://${normalizedApiFqdn}'");
  assert.equal(variableValue('redirectUri'), "'${apiBaseUri}/api/v1/auth/oidc/entra/callback'");
  assert.equal(variableValue('signOutUri'), "'${apiBaseUri}/api/v1/auth/logout'");

  const web = property(APPLICATION.body, 'web');
  assert.equal(collapse(property(web, 'redirectUris')), '[ redirectUri ]');
  assert.equal(property(web, 'logoutUrl'), 'signOutUri');
  assert.equal(property(web, 'homePageUrl'), 'apiBaseUri');

  // A web-only confidential client: no other platform may hold a reply URI.
  assert.equal(collapse(property(APPLICATION.body, 'publicClient')), '{ redirectUris: [] }');
  assert.equal(collapse(property(APPLICATION.body, 'spa')), '{ redirectUris: [] }');

  for (const literal of stringLiterals(MODULE)) {
    assert.ok(!literal.includes('*'), `wildcard found in string literal '${literal}'`);
    assert.ok(!literal.includes('http://'), `plaintext HTTP found in string literal '${literal}'`);
  }
});

test('no id token, access token, or groups claim is requested implicitly', () => {
  const web = property(APPLICATION.body, 'web');
  assert.equal(
    collapse(property(web, 'implicitGrantSettings')),
    '{ enableAccessTokenIssuance: false enableIdTokenIssuance: false }',
  );
  assert.equal(property(APPLICATION.body, 'groupMembershipClaims'), "'None'");
  assert.equal(collapse(property(APPLICATION.body, 'optionalClaims')), '{ accessToken: [] idToken: [] saml2Token: [] }');
});

test('the application requests only the openid, profile, and email delegated scopes', () => {
  const requested = collapse(property(APPLICATION.body, 'requiredResourceAccess'));
  assert.equal(
    requested,
    '[ { resourceAppId: microsoftGraphAppId resourceAccess: [ { id: openIdScopeId type: \'Scope\' } '
      + '{ id: profileScopeId type: \'Scope\' } { id: emailScopeId type: \'Scope\' } ] } ]',
  );
  assert.equal(variableValue('microsoftGraphAppId'), "'00000003-0000-0000-c000-000000000000'");
  assert.equal(variableValue('openIdScopeId'), "'37f7f235-527c-4136-accd-4a02d197296e'");
  assert.equal(variableValue('profileScopeId'), "'14dad69e-099b-42c9-810b-d002981feec1'");
  assert.equal(variableValue('emailScopeId'), "'64a6cdd6-aab1-7aab-01b2-9bfd0e2391de'");
});

test('the application declares no secret credential of any kind', () => {
  assert.equal(collapse(property(APPLICATION.body, 'passwordCredentials')), '[]');
  assert.equal(collapse(property(APPLICATION.body, 'keyCredentials')), '[]');
  assert.equal(property(APPLICATION.body, 'isFallbackPublicClient'), 'false');
  assert.ok(!hasProperty(APPLICATION.body, 'tokenEncryptionKeyId'));

  assert.doesNotMatch(MODULE, /@secure\s*\(/);
  assert.doesNotMatch(MODULE, /\blistKeys\s*\(|\blistSecrets\s*\(|\bgetSecret\s*\(/);
  assert.doesNotMatch(MODULE, /Microsoft\.KeyVault/);
  assert.doesNotMatch(MODULE, /\bclientSecret\b/i);
});

test('the federated identity credential carries the exact trusted issuer, subject, and audience', () => {
  assert.equal(FEDERATED_CREDENTIAL.type, 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0');
  assert.equal(variableValue('federatedCredentialName'), "'api-managed-identity'");
  assert.equal(
    variableValue('federatedCredentialIssuer'),
    "'https://login.microsoftonline.com/${normalizedTenantId}/v2.0'",
  );
  assert.equal(variableValue('federatedCredentialAudience'), "'api://AzureADTokenExchange'");

  // Child of the application through the Graph alternate key path.
  assert.equal(property(FEDERATED_CREDENTIAL.body, 'name'), "'${application.uniqueName}/${federatedCredentialName}'");
  assert.equal(property(FEDERATED_CREDENTIAL.body, 'issuer'), 'federatedCredentialIssuer');
  assert.equal(property(FEDERATED_CREDENTIAL.body, 'subject'), 'normalizedApiIdentityPrincipalId');
  assert.equal(collapse(property(FEDERATED_CREDENTIAL.body, 'audiences')), '[ federatedCredentialAudience ]');

  // A bad issuer/subject/audience deploys clean and fails only at token
  // exchange, so none of the three may be widened or made optional.
  const issuer = variableValue('federatedCredentialIssuer').slice(1, -1);
  assert.equal(issuer, issuer.trim(), 'the issuer must not carry leading or trailing whitespace');
  assert.ok(issuer.endsWith('/v2.0'), 'the issuer must be the v2.0 endpoint');
});

test('the service principal is created from the application and exposes only its object ID', () => {
  assert.equal(SERVICE_PRINCIPAL.type, 'Microsoft.Graph/servicePrincipals@v1.0');
  assert.equal(collapse(SERVICE_PRINCIPAL.body), '{ appId: application.appId }');
});

test('every input is whitespace and case normalized before it reaches Graph', () => {
  assert.equal(variableValue('normalizedUniqueName'), 'toLower(trim(uniqueName))');
  assert.equal(variableValue('normalizedDisplayName'), 'trim(displayName)');
  assert.equal(variableValue('normalizedApiFqdn'), 'toLower(trim(apiFqdn))');
  assert.equal(variableValue('normalizedApiIdentityPrincipalId'), 'toLower(trim(apiIdentityPrincipalId))');
  assert.equal(variableValue('normalizedTenantId'), 'toLower(trim(tenantId))');

  // No raw parameter may reach a resource: every use outside its declaration
  // and its normalizing var (member access and property keys excluded) is a
  // path where a stray space or an uppercased GUID would reach Entra verbatim.
  const residual = MODULE.split('\n')
    .filter((line) => !/^param\s/.test(line) && !/^var\s+normalized/.test(line))
    .join('\n')
    .replace(/'(?:[^'\\\n]|\\.)*'/g, "''");

  for (const parameter of FROZEN_PARAMETERS) {
    const rawUses = [...residual.matchAll(new RegExp(`(^|[^.\\w])${parameter}\\b(?!\\s*:)`, 'g'))];
    assert.equal(rawUses.length, 0, `${parameter} is used without normalization`);
  }
});

test('the deployment is idempotent and deterministic', () => {
  // `uniqueName` is the Graph alternate key: redeploying upserts the same app.
  assert.equal(property(APPLICATION.body, 'uniqueName'), 'normalizedUniqueName');
  assert.doesNotMatch(MODULE, /\bnewGuid\s*\(|\butcNow\s*\(|\buniqueString\s*\(|\bguid\s*\(|\bdateTimeAdd\s*\(/);
  assert.doesNotMatch(MODULE, /\bexisting\b/);
  // Graph resources only; this module must never provision ARM resources.
  for (const match of MODULE.matchAll(/^resource\s+\w+\s+'([^']+)'/gm)) {
    assert.match(match[1], /^Microsoft\.Graph\//);
  }
});

test('the unique name stays inside the Graph alternate key limit for the longest environment name', () => {
  const uniqueNameParameter = PARAMETERS.find((parameter) => parameter.name === 'uniqueName');
  const maxLength = uniqueNameParameter.decorators
    .map((decorator) => /^@maxLength\((\d+)\)$/.exec(decorator))
    .find(Boolean);

  assert.ok(maxLength, 'uniqueName must declare @maxLength');
  assert.ok(
    Number(maxLength[1]) <= UNIQUE_NAME_MAX_LENGTH,
    `uniqueName @maxLength must not exceed the Graph limit of ${UNIQUE_NAME_MAX_LENGTH}`,
  );

  const longestDerivedName = UNIQUE_NAME_PREFIX + 'e'.repeat(AZD_ENVIRONMENT_NAME_MAX_LENGTH);
  assert.ok(
    longestDerivedName.length <= Number(maxLength[1]),
    `vistara-<environmentName> must fit: ${longestDerivedName.length} > ${maxLength[1]}`,
  );
});

test('the module documents the --skip-app-registration fallback that HB-12 depends on', () => {
  const header = RAW_MODULE.slice(0, RAW_MODULE.indexOf('targetScope'));
  for (const expected of [
    '--skip-app-registration',
    'deployAppRegistration',
    'existingApplicationClientId',
    'az ad app create',
    'az ad app federated-credential create',
    'postprovision-verify-fic.sh',
  ]) {
    assert.ok(header.includes(expected), `the module header must document '${expected}'`);
  }
});
