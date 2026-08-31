import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const INFRA = resolve(REPOSITORY_ROOT, 'deploy/azure/infra');
const MODULES = resolve(INFRA, 'modules');

const MAIN = readFileSync(resolve(INFRA, 'main.bicep'), 'utf8');
const PARAMETERS = readFileSync(resolve(INFRA, 'main.parameters.json'), 'utf8');
const CONFIG_CONTRACT = JSON.parse(
  readFileSync(resolve(HERE, 'fixtures/azure-config-contract.json'), 'utf8'),
);

/** Minimum Bicep compiler the hosted bootstrap preflight accepts. */
const BICEP_MINIMUM_VERSION = '0.36.1';

function moduleSource(name) {
  return readFileSync(resolve(MODULES, name), 'utf8');
}

function declaredParameters(source) {
  return [...source.matchAll(/^param\s+([A-Za-z0-9_]+)\s/gm)].map(
    (match) => match[1],
  );
}

function declaredOutputs(source) {
  return [...source.matchAll(/^output\s+([A-Za-z0-9_]+)\s/gm)].map(
    (match) => match[1],
  );
}

/**
 * Resolves the azd `${VAR=default}` placeholders the way azd does before the
 * file is handed to ARM, so the template can be parsed as JSON.
 */
function renderParameterFile(source) {
  return source.replace(/\$\{([A-Z0-9_]+)(?:=([^}]*))?\}/g, (_, __, fallback) =>
    fallback === undefined ? 'placeholder' : fallback,
  );
}

/**
 * Reads `var name = 'literal'` declarations so an interpolated environment
 * variable name can be resolved to the string the deployment actually emits.
 */
function literalVariables(source) {
  return new Map(
    [...source.matchAll(/^var\s+([A-Za-z0-9_]+)\s+=\s+'([^'$]*)'$/gm)].map(
      (match) => [match[1], match[2]],
    ),
  );
}

/**
 * Every configuration or process variable name the template hands to a
 * container, with single-variable interpolations resolved.
 */
function emittedEnvironmentNames(source) {
  const literals = literalVariables(source);
  const names = new Set();
  for (const [, raw] of source.matchAll(/^\s*name: '([^']+)'$/gm)) {
    const resolved = raw.replace(
      /\$\{([A-Za-z0-9_]+)\}/g,
      (whole, reference) => literals.get(reference) ?? whole,
    );
    if (/^[A-Za-z][A-Za-z0-9]*(__[A-Za-z0-9]+)+$/.test(resolved) ||
      /^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$/.test(resolved)) {
      names.add(resolved);
    }
  }
  return names;
}

function compareVersions(left, right) {
  const a = left.split('.').map(Number);
  const b = right.split('.').map(Number);
  for (let index = 0; index < Math.max(a.length, b.length); index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }
  return 0;
}

const EXPECTED_PARAMETERS = [
  'environmentName',
  'location',
  'resourceGroupName',
  'apiImage',
  'workerImage',
  'migrationImage',
  'registryServer',
  'registryUsername',
  'registryPasswordSecretUri',
  'entraTenantId',
  'firstOwnerObjectId',
  'postgresSku',
  'postgresStorageGb',
  'postgresEntraAdminObjectId',
  'postgresEntraAdminPrincipalName',
  'postgresEntraAdminPrincipalType',
  'storageRedundancy',
  'apiMinReplicas',
  'apiMaxReplicas',
  'workerMinReplicas',
  'workerMaxReplicas',
  'budgetAmount',
  'budgetContactEmails',
  'budgetStartDate',
  'customDomainName',
  'customDomainCertificateId',
  'deployAppRegistration',
  'existingApplicationClientId',
  'deployApplications',
  'apiKeyPepperSecretUri',
  'retainData',
];

const EXPECTED_OUTPUTS = [
  'AZURE_TENANT_ID',
  'AZURE_RESOURCE_GROUP',
  'AZURE_LOCATION',
  'SERVICE_API_URI',
  'ENTRA_APPLICATION_CLIENT_ID',
  'API_IDENTITY_CLIENT_ID',
  'API_IDENTITY_PRINCIPAL_ID',
  'WORKER_IDENTITY_CLIENT_ID',
  'WORKER_IDENTITY_PRINCIPAL_ID',
  'MIGRATE_IDENTITY_CLIENT_ID',
  'MIGRATE_IDENTITY_PRINCIPAL_ID',
  'POSTGRES_HOST',
  'POSTGRES_DATABASE',
  'POSTGRES_API_ROLE',
  'POSTGRES_WORKER_ROLE',
  'POSTGRES_MIGRATOR_ROLE',
  'AZURE_STORAGE_ACCOUNT_NAME',
  'AZURE_STORAGE_BLOB_ENDPOINT',
  'MEDIA_CONTAINER_NAME',
  'DATAPROTECTION_BLOB_URI',
  'DATAPROTECTION_KEY_ID',
  'AZURE_KEY_VAULT_ENDPOINT',
  'MIGRATION_JOB_NAME',
  'API_CONTAINER_APP_NAME',
  'WORKER_CONTAINER_APP_NAME',
];

const EXPECTED_MODULES = [
  'api.bicep',
  'budget.bicep',
  'environment.bicep',
  'identity.bicep',
  'keyvault.bicep',
  'migrate-job.bicep',
  'monitoring.bicep',
  'postgres.bicep',
  'rbac.bicep',
  'storage.bicep',
  'worker.bicep',
];

test('main.bicep declares exactly the agreed parameter surface', () => {
  assert.deepEqual(declaredParameters(MAIN), EXPECTED_PARAMETERS);
});

test('main.bicep emits exactly the agreed output surface', () => {
  assert.deepEqual(declaredOutputs(MAIN), EXPECTED_OUTPUTS);
});

test('main.bicep deploys at subscription scope and owns the resource group', () => {
  assert.match(MAIN, /^targetScope = 'subscription'$/m);
  assert.match(MAIN, /Microsoft\.Resources\/resourceGroups@\d{4}-\d{2}-\d{2}'/);
  assert.match(MAIN, /'azd-env-name': environmentName/);
});

test('every module referenced by main.bicep exists and is itself modular', () => {
  const referenced = [...MAIN.matchAll(/'(modules\/[a-z-]+\.bicep)'/g)].map(
    (match) => match[1],
  );
  assert.ok(referenced.length > 0, 'main.bicep must compose modules');
  for (const path of referenced) {
    assert.ok(
      existsSync(resolve(INFRA, path)),
      `main.bicep references missing module ${path}`,
    );
  }
  assert.deepEqual(
    readdirSync(MODULES).filter((entry) => entry.endsWith('.bicep')).sort(),
    EXPECTED_MODULES,
  );
});

test('main.bicep composes without the separately owned Entra module', () => {
  assert.doesNotMatch(MAIN, /module\s+\w+\s+'entra\//);
  assert.doesNotMatch(MAIN, /'\.{0,2}\/?entra\/[a-z-]+\.bicep'/);
  assert.match(MAIN, /param existingApplicationClientId string = ''/);
  assert.match(MAIN, /output ENTRA_APPLICATION_CLIENT_ID string = applicationClientId/);
});

test('every main.bicep parameter carries operator documentation', () => {
  const undocumented = [
    ...MAIN.matchAll(/(@description\('[^']*'\)\s*)?(?:@[A-Za-z]+\([^)]*\)\s*|@[A-Za-z]+\(\[[^\]]*\]\)\s*)*^param\s+([A-Za-z0-9_]+)/gms),
  ];
  for (const name of EXPECTED_PARAMETERS) {
    const pattern = new RegExp(
      `@description\\('[^']+'\\)[\\s\\S]{0,400}?^param ${name} `,
      'm',
    );
    assert.match(MAIN, pattern, `parameter ${name} needs a @description`);
  }
  assert.ok(undocumented.length > 0);
});

test('container images must be digest pinned', () => {
  for (const image of ['apiImage', 'workerImage', 'migrationImage']) {
    assert.match(
      MAIN,
      new RegExp(`substring\\(${image}, indexOf\\(${image}, '@sha256:'\\)\\)`),
      `${image} must fail the deployment when it is not digest pinned`,
    );
  }
});

test('no template declares a secure parameter or a literal secret value', () => {
  const sources = [MAIN, ...EXPECTED_MODULES.map(moduleSource)];
  for (const source of sources) {
    assert.doesNotMatch(source, /@secure\(\)/);
    assert.doesNotMatch(source, /administratorLoginPassword/);
    assert.doesNotMatch(source, /administratorLogin\b/);
    assert.doesNotMatch(source, /listSecrets\(/);
    assert.doesNotMatch(source, /listAccountSas|listServiceSas/);
  }
});

test('no output exposes a key, secret, connection string, or token', () => {
  const sources = [MAIN, ...EXPECTED_MODULES.map(moduleSource)];
  for (const source of sources) {
    for (const [, name] of source.matchAll(/^output\s+([A-Za-z0-9_]+)\s/gm)) {
      assert.doesNotMatch(
        name,
        /secret|password|sharedkey|accountkey|token|sas|connectionstring/i,
        `output ${name} looks like it carries credential material`,
      );
    }
    assert.doesNotMatch(source, /^output\s+\w+\s+string\s*=\s*.*listKeys\(/m);
  }
});

test('the only workspace key read is the deployment-time Log Analytics shared key', () => {
  const usages = [MAIN, ...EXPECTED_MODULES.map(moduleSource)]
    .flatMap((source) => [...source.matchAll(/listKeys\(\)/g)])
    .length;
  assert.equal(usages, 1);
  assert.match(
    moduleSource('environment.bicep'),
    /sharedKey: logAnalyticsWorkspace\.listKeys\(\)\.primarySharedKey/,
  );
});

test('every resource type pins a stable, non-preview API version', () => {
  const sources = [MAIN, ...EXPECTED_MODULES.map(moduleSource)];
  for (const source of sources) {
    for (const [, apiVersion] of source.matchAll(
      /^resource\s+\w+\s+'[^']+@([^']+)'/gm,
    )) {
      assert.match(apiVersion, /^\d{4}-\d{2}-\d{2}$/, `${apiVersion} is not stable`);
    }
  }
});

test('storage refuses account keys, SAS, and public blob access', () => {
  const storage = moduleSource('storage.bicep');
  assert.match(storage, /allowSharedKeyAccess: false/);
  assert.match(storage, /allowBlobPublicAccess: false/);
  assert.match(storage, /defaultToOAuthAuthentication: true/);
  assert.match(storage, /supportsHttpsTrafficOnly: true/);
  assert.match(storage, /minimumTlsVersion: 'TLS1_2'/);
  const containers = [...storage.matchAll(/publicAccess: 'None'/g)];
  assert.equal(containers.length, 2, 'media and dataprotection must both be private');
});

test('key vault uses RBAC authorization and holds a wrap-only Data Protection key', () => {
  const keyVault = moduleSource('keyvault.bicep');
  assert.match(keyVault, /enableRbacAuthorization: true/);
  assert.match(keyVault, /enableSoftDelete: true/);
  assert.doesNotMatch(keyVault, /enablePurgeProtection: false/);
  assert.match(keyVault, /kty: 'RSA'/);
  assert.match(keyVault, /'wrapKey'\s+'unwrapKey'/);
  assert.match(keyVault, /output dataProtectionKeyId string = dataProtectionKey\.properties\.keyUri/);
});

test('postgres accepts Entra tokens only', () => {
  const postgres = moduleSource('postgres.bicep');
  assert.match(postgres, /activeDirectoryAuth: 'Enabled'/);
  assert.match(postgres, /passwordAuth: 'Disabled'/);
  assert.match(
    postgres,
    /resource administrator 'Microsoft\.DBforPostgreSQL\/flexibleServers\/administrators@/,
  );
  assert.match(postgres, /principalType: entraAdminPrincipalType/);
});

test('connection strings never carry a password', () => {
  assert.match(MAIN, /Username=\$\{role\}/);
  assert.match(MAIN, /SSL Mode=VerifyFull/);
  assert.doesNotMatch(MAIN, /Password=/i);
});

test('the API declares explicit HTTP probes for all three health routes', () => {
  const api = moduleSource('api.bicep');
  for (const [type, path] of [
    ['Startup', '/health/startup'],
    ['Readiness', '/health/ready'],
    ['Liveness', '/health/live'],
  ]) {
    const probe = new RegExp(
      `type: '${type}'[\\s\\S]{0,200}?httpGet: \\{[\\s\\S]{0,200}?path: '${path}'`,
    );
    assert.match(api, probe, `${type} probe must be an HTTP GET on ${path}`);
  }
  assert.doesNotMatch(api, /tcpSocket/);
});

test('the API ingress is public HTTPS only and tagged for azd deploy', () => {
  const api = moduleSource('api.bicep');
  assert.match(api, /external: true/);
  assert.match(api, /allowInsecure: false/);
  assert.match(api, /transport: 'auto'/);
  assert.match(api, /targetPort: targetPort/);
  assert.match(MAIN, /'azd-service-name': 'api'/);
  assert.match(MAIN, /'azd-service-name': 'worker'/);
});

test('the API never redirects to HTTPS in process', () => {
  // Container Apps refuses plain HTTP at the edge, and the forwarded-header
  // middleware trusts no proxy address, so an in-process redirect would loop.
  assert.match(
    MAIN,
    /name: 'Security__Transport__RedirectHttpToHttps'\s*\n\s*value: 'false'/,
  );
  assert.match(moduleSource('api.bicep'), /allowInsecure: false/);
});

test('activation is a separate provision pass that defaults to off', () => {
  assert.match(MAIN, /param deployApplications bool = false/);
  assert.match(MAIN, /module api 'modules\/api\.bicep' = if \(deployApplications\)/);
  assert.match(
    MAIN,
    /module worker 'modules\/worker\.bicep' = if \(deployApplications\)/,
  );
  // The migration job must exist on the first pass so it can be run before any
  // application replica starts.
  assert.match(MAIN, /module migrationJob 'modules\/migrate-job\.bicep' = \{/);
});

test('outputs stay resolvable while the applications are still off', () => {
  assert.match(
    MAIN,
    /output API_CONTAINER_APP_NAME string = deployApplications \? api!\.outputs\.name : ''/,
  );
  assert.match(
    MAIN,
    /output WORKER_CONTAINER_APP_NAME string = deployApplications \? worker!\.outputs\.name : ''/,
  );
  // The ingress host is derived from the environment domain, so the callback
  // URI is known before the container app exists.
  assert.match(MAIN, /var apiDefaultFqdn = '\$\{apiAppName\}\.\$\{containerAppsEnvironment\.outputs\.defaultDomain\}'/);
  assert.doesNotMatch(MAIN, /output SERVICE_API_URI string = deployApplications/);
});

test('the pepper reaches the API as a Key Vault reference on the activation pass', () => {
  assert.match(MAIN, /param apiKeyPepperSecretUri string = ''/);
  assert.match(MAIN, /keyVaultUrl: apiKeyPepperSecretUriChecked/);
  assert.match(
    MAIN,
    /name: 'Platform__Authentication__ApiKeys__Peppers__\$\{apiKeyPepperVersion\}'\s*\n\s*secretRef: apiKeyPepperSecretName/,
  );
  assert.match(
    MAIN,
    /name: 'Security__RequiredSecretKeys__0'\s*\n\s*value: apiKeyPepperConfigurationKey/,
  );
  // A missing or malformed secret URI must stop the deployment rather than
  // reach Container Apps as an unresolvable reference.
  assert.match(
    MAIN,
    /substring\(apiKeyPepperSecretUri, indexOf\(apiKeyPepperSecretUri, '\/secrets\/'\)\)/,
  );
  assert.doesNotMatch(MAIN, /apiKeyPepper\w*\s*=\s*'[A-Za-z0-9+/=]{16,}'/);
});

test('every identity is named explicitly for the Azure SDK and for its options', () => {
  const clientIdVariables = [
    ...MAIN.matchAll(/name: 'AZURE_CLIENT_ID'\s*\n\s*value: identity\.outputs\.(\w+)/g),
  ].map((match) => match[1]);
  assert.deepEqual(clientIdVariables.sort(), [
    'apiClientId',
    'migrateClientId',
    'workerClientId',
  ]);
  assert.match(MAIN, /name: 'Persistence__Azure__ManagedIdentityClientId'/);
  assert.match(MAIN, /name: 'Media__Storage__Azure__ManagedIdentityClientId'/);
  assert.match(MAIN, /name: 'Security__DataProtection__ManagedIdentityClientId'/);
  assert.match(MAIN, /name: 'MIGRATION_MANAGED_IDENTITY_CLIENT_ID'/);
});

test('the worker has no ingress and therefore no probes', () => {
  const worker = moduleSource('worker.bicep');
  assert.doesNotMatch(worker, /^\s*ingress:/m);
  assert.doesNotMatch(worker, /^\s*probes:/m);
  assert.doesNotMatch(worker, /^\s*targetPort:/m);
  assert.match(worker, /param maxReplicas int = 1/);
});

test('scale bounds are parameterised on both container apps', () => {
  for (const name of ['api.bicep', 'worker.bicep']) {
    const source = moduleSource(name);
    assert.match(source, /minReplicas: minReplicas/);
    assert.match(source, /maxReplicas: maxReplicas/);
  }
});

test('the migration job is a manually triggered one-shot', () => {
  const job = moduleSource('migrate-job.bicep');
  assert.match(job, /triggerType: 'Manual'/);
  assert.match(job, /replicaRetryLimit: 0/);
  assert.match(job, /parallelism: 1/);
  assert.match(job, /replicaCompletionCount: 1/);
  assert.match(MAIN, /name: 'MIGRATION_PROVIDER'\s*\n\s*value: 'PostgreSql'/);
});

test('role assignments use the reviewed built-in role identifiers', () => {
  const rbac = moduleSource('rbac.bicep');
  assert.match(rbac, /ba92f5b4-2d11-453d-a403-e96b0029c9fe/);
  assert.match(rbac, /12338af0-0e69-4776-bea7-57ae8d297424/);
  assert.match(rbac, /4633458b-17de-408a-b874-0445c86b69e6/);
  assert.match(rbac, /db58b8e5-c6ad-4a2a-8342-4190687cbf4a/);
  const assignments = [
    ...rbac.matchAll(/Microsoft\.Authorization\/roleAssignments@/g),
  ];
  assert.equal(assignments.length, 9);
  assert.match(rbac, /scope: storageAccount::blobService::mediaContainer/);
  assert.match(rbac, /scope: storageAccount::blobService::dataProtectionContainer/);
});

test('user delegation signing is account scoped while data access stays container scoped', () => {
  const rbac = moduleSource('rbac.bicep');
  for (const principal of ['apiPrincipalId', 'workerPrincipalId']) {
    const delegator = new RegExp(
      `name: guid\\(\\s*storageAccount\\.id,\\s*${principal},\\s*storageBlobDelegatorRoleId\\s*\\)\\s*\\n\\s*scope: storageAccount\\n`,
    );
    assert.match(rbac, delegator, `${principal} needs account-scoped Blob Delegator`);
  }
  assert.doesNotMatch(
    rbac,
    /scope: storageAccount\n(?:(?!roleAssignments)[\s\S])*?storageBlobDataContributorRoleId/,
    'blob data roles must never widen to the account',
  );
});

test('the migration identity reads Key Vault only for a private registry', () => {
  const rbac = moduleSource('rbac.bicep');
  assert.match(
    rbac,
    /resource migrateKeyVaultSecretsUser 'Microsoft\.Authorization\/roleAssignments@[\d-]+' = if \(grantMigrateKeyVaultSecretsUser\)/,
  );
  assert.match(rbac, /principalId: migratePrincipalId/);
  assert.match(MAIN, /grantMigrateKeyVaultSecretsUser: privateRegistry/);
});

test('data resources are locked whenever retention is requested', () => {
  for (const name of ['storage.bicep', 'keyvault.bicep', 'postgres.bicep']) {
    const source = moduleSource(name);
    assert.match(
      source,
      /Microsoft\.Authorization\/locks@\d{4}-\d{2}-\d{2}' = if \(retainData\)/,
      `${name} must lock its data resource when retainData is set`,
    );
    assert.match(source, /level: 'CanNotDelete'/);
    assert.match(source, /param retainData bool = true/);
  }
  assert.match(MAIN, /param retainData bool = true/);
  assert.match(MAIN, /'azd-retain': string\(retainData\)/);
});

test('the budget alerts on actual and forecast spend', () => {
  const budget = moduleSource('budget.bicep');
  assert.match(budget, /category: 'Cost'/);
  assert.match(budget, /timeGrain: 'Monthly'/);
  assert.match(budget, /thresholdType: 'Actual'/);
  assert.match(budget, /thresholdType: 'Forecasted'/);
  assert.match(MAIN, /param budgetStartDate string = utcNow\('yyyy-MM-01'\)/);
});

test('a private registry password only ever travels as a Key Vault reference', () => {
  assert.match(MAIN, /keyVaultUrl: registryPasswordSecretUri/);
  assert.doesNotMatch(MAIN, /passwordSecretRef: registryPassword\b/);
  assert.match(MAIN, /passwordSecretRef: registryPasswordSecretName/);
  for (const name of ['api.bicep', 'worker.bicep', 'migrate-job.bicep']) {
    assert.doesNotMatch(moduleSource(name), /value: secret/i);
  }
});

test('main.parameters.json renders to JSON that matches the template surface', () => {
  const rendered = JSON.parse(renderParameterFile(PARAMETERS));
  assert.equal(rendered.contentVersion, '1.0.0.0');
  const supplied = Object.keys(rendered.parameters);
  for (const name of supplied) {
    assert.ok(
      EXPECTED_PARAMETERS.includes(name),
      `main.parameters.json supplies unknown parameter ${name}`,
    );
  }
  for (const name of [
    'environmentName',
    'location',
    'apiImage',
    'workerImage',
    'migrationImage',
    'firstOwnerObjectId',
    'postgresEntraAdminObjectId',
    'postgresEntraAdminPrincipalName',
  ]) {
    assert.ok(
      supplied.includes(name),
      `main.parameters.json must supply required parameter ${name}`,
    );
  }
  assert.equal(typeof rendered.parameters.budgetAmount.value, 'number');
  assert.equal(typeof rendered.parameters.retainData.value, 'boolean');
  assert.ok(Array.isArray(rendered.parameters.budgetContactEmails.value));
});

test('main.parameters.json never carries a secret value', () => {
  // A Key Vault secret URI names a secret; it is not one. Anything else that
  // reads like credential material must not appear at all.
  assert.doesNotMatch(PARAMETERS, /PASSWORD(?!_SECRET_URI)/);
  assert.doesNotMatch(PARAMETERS, /PEPPER(?!_SECRET_URI)/);
  assert.doesNotMatch(PARAMETERS, /CLIENT_SECRET|ACCOUNT_KEY|SHARED_KEY/);
  for (const [, value] of PARAMETERS.matchAll(/"value": "([^"]*)"/g)) {
    assert.ok(
      value === '' || /^\$\{[A-Z0-9_]+(=[^}]*)?\}$/.test(value),
      `main.parameters.json inlines the literal '${value}'`,
    );
  }
});

test('every emitted configuration key is declared in the config contract', () => {
  const declared = new Map(
    CONFIG_CONTRACT.keys.map((entry) => [entry.name, entry]),
  );
  const emitted = emittedEnvironmentNames(MAIN);
  assert.ok(emitted.size > 40, 'the extractor found suspiciously few names');
  for (const name of emitted) {
    assert.ok(
      declared.has(name),
      `main.bicep emits '${name}', which no options property in `
        + 'eng/tests/fixtures/azure-config-contract.json accounts for',
    );
  }
});

test('every required contract key is actually emitted', () => {
  const emitted = emittedEnvironmentNames(MAIN);
  for (const entry of CONFIG_CONTRACT.keys) {
    if (!entry.required) {
      continue;
    }

    assert.ok(
      emitted.has(entry.name),
      `the config contract requires '${entry.name}' but main.bicep never emits it`,
    );
  }
});

test('contract entries name a property that the owning source really declares', () => {
  let verified = 0;
  let pending = 0;
  for (const entry of CONFIG_CONTRACT.keys) {
    if (!entry.sourceFile) {
      continue;
    }

    const path = resolve(REPOSITORY_ROOT, entry.sourceFile);
    const source = existsSync(path) ? readFileSync(path, 'utf8') : null;
    const declared = entry.kind === 'options'
      ? source !== null &&
        new RegExp(`class ${entry.optionsType}\\b`).test(source) &&
        new RegExp(`\\b${entry.property}\\b\\s*\\{\\s*get;`).test(source)
      : source !== null && source.includes(entry.sourceToken);

    if (entry.pendingOwner) {
      // The marker must be removed the moment the sibling task lands, or the
      // contract silently stops checking a key that is now verifiable.
      assert.equal(
        declared,
        false,
        `${entry.pendingOwner} has landed: '${entry.name}' is now declared in `
          + `${entry.sourceFile}, so drop its pendingOwner marker`,
      );
      pending += 1;
      continue;
    }

    assert.ok(
      declared,
      entry.kind === 'options'
        ? `${entry.sourceFile} must declare ${entry.optionsType}.${entry.property} `
          + `for configuration key '${entry.name}'`
        : `${entry.sourceFile} must read '${entry.sourceToken}' for '${entry.name}'`,
    );
    verified += 1;
  }

  assert.ok(verified > 15, `only ${verified} contract entries could be verified`);
  assert.ok(pending > 0, 'pending markers vanished without the contract changing');
});

test('pending contract entries name a real sibling task', () => {
  const owners = new Set(
    CONFIG_CONTRACT.keys
      .filter((entry) => entry.pendingOwner)
      .map((entry) => entry.pendingOwner),
  );
  assert.ok(owners.size > 0);
  for (const owner of owners) {
    assert.match(owner, /^HB-\d{2}$/, `'${owner}' is not a hosted bootstrap task id`);
  }
});

test('configuration keys carrying an identity are wired to a managed identity', () => {
  const declared = new Map(
    CONFIG_CONTRACT.keys.map((entry) => [entry.name, entry]),
  );
  for (const [name, entry] of declared) {
    if (!/ManagedIdentityClientId$|^AZURE_CLIENT_ID$|CLIENT_ID$/.test(name)) {
      continue;
    }

    assert.notEqual(entry.kind, undefined);
    if (!MAIN.includes(`'${name}'`)) {
      continue;
    }

    const assignment = new RegExp(
      `name: '${name.replace(/[$]/g, '\\$')}'\\s*\\n\\s*value: identity\\.outputs\\.\\w+ClientId`,
    );
    assert.match(MAIN, assignment, `${name} must be bound to a user-assigned identity`);
  }
});

test('only the pepper is delivered through a container secret reference', () => {
  const secretReferences = [...MAIN.matchAll(/^\s*secretRef: (\w+)$/gm)].map(
    (match) => match[1],
  );
  assert.deepEqual(secretReferences, ['apiKeyPepperSecretName']);
  const declaredSecretKeys = CONFIG_CONTRACT.keys.filter(
    (entry) => entry.secretReference,
  );
  assert.equal(declaredSecretKeys.length, 1);
  assert.equal(
    declaredSecretKeys[0].name,
    'Platform__Authentication__ApiKeys__Peppers__v1',
  );
});

test('the pinned bicep compiler builds and lints the template', (t) => {
  const bicep = process.env.BICEP ?? 'bicep';
  const probe = spawnSync(bicep, ['--version'], { encoding: 'utf8' });
  if (probe.error || probe.status !== 0) {
    // CI provisions a pinned compiler, so an unavailable binary there is a
    // failure rather than a silently green run.
    assert.ok(
      !process.env.CI,
      `bicep is required in CI but '${bicep}' could not be executed: `
        + `${probe.error?.message ?? `exit ${probe.status}`}`,
    );
    t.skip(`bicep binary not available (${bicep})`);
    return;
  }

  const version = /(\d+\.\d+\.\d+)/.exec(probe.stdout)?.[1];
  assert.ok(version, `could not read a version from '${probe.stdout.trim()}'`);
  assert.ok(
    compareVersions(version, BICEP_MINIMUM_VERSION) >= 0,
    `bicep ${version} is older than the pinned minimum ${BICEP_MINIMUM_VERSION}`,
  );

  const build = spawnSync(
    bicep,
    ['build', '--stdout', resolve(INFRA, 'main.bicep')],
    { encoding: 'utf8' },
  );
  assert.equal(build.status, 0, build.stderr);
  assert.equal(build.stderr.trim(), '', `bicep reported diagnostics:\n${build.stderr}`);

  const template = JSON.parse(build.stdout);
  assert.deepEqual(Object.keys(template.outputs), EXPECTED_OUTPUTS);
  assert.deepEqual(Object.keys(template.parameters), EXPECTED_PARAMETERS);
  for (const parameter of Object.values(template.parameters)) {
    assert.notEqual(parameter.type, 'secureString');
    assert.notEqual(parameter.type, 'secureObject');
  }

  const rendered = JSON.stringify(template);
  assert.doesNotMatch(rendered, /listSecrets\(/);
  assert.equal((rendered.match(/listKeys\(/g) ?? []).length, 1);

  for (const module of EXPECTED_MODULES) {
    const lint = spawnSync(bicep, ['lint', resolve(MODULES, module)], {
      encoding: 'utf8',
    });
    assert.equal(lint.status, 0, lint.stderr);
    assert.equal(
      lint.stderr.trim(),
      '',
      `bicep lint reported diagnostics for ${module}:\n${lint.stderr}`,
    );
  }
});
