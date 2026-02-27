// Hybrid Worker package upload — runs inside the VNet via ACI deploymentScript.
// Deployed by deploy-apps.yml on every code push. The infrastructure resources
// (managed identity, script storage, RBAC) live in deploy.bicep and are
// deployed once via deploy-infra.yml.

@description('Name of the update storage account (target for blob upload).')
param updateStorageAccountName string

@description('Name of the script storage account (deployment script runtime).')
param scriptStorageAccountName string

@description('Resource ID of the user-assigned managed identity for the deployment script.')
param managedIdentityId string

@description('Client ID of the user-assigned managed identity (for az login).')
param managedIdentityClientId string

@description('Resource ID of the ACI subnet for VNet-integrated deployment scripts.')
param subnetId string

@description('Azure region.')
param location string = resourceGroup().location

@description('Hybrid worker package version string (e.g. 1.0.42).')
param packageVersion string

@description('Expected SHA256 hash of the package zip.')
param packageSha256 string

@secure()
@description('Base64-encoded hybrid worker zip package.')
param packageBase64 string

param tags object = { component: 'hybrid-worker', project: 'ma-toolkit' }
param deployTimestamp string = utcNow()

resource uploadScript 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'hw-upload-${take(deployTimestamp, 13)}'
  location: location
  tags: tags
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    azCliVersion: '2.60.0'
    retentionInterval: 'PT1H'
    timeout: 'PT15M'
    cleanupPreference: 'OnSuccess'
    forceUpdateTag: deployTimestamp
    containerSettings: {
      subnetIds: [
        { id: subnetId }
      ]
    }
    storageAccountSettings: {
      storageAccountName: scriptStorageAccountName
    }
    environmentVariables: [
      { name: 'STORAGE_ACCOUNT', value: updateStorageAccountName }
      { name: 'CONTAINER_NAME', value: 'hybrid-worker' }
      { name: 'PACKAGE_VERSION', value: packageVersion }
      { name: 'PACKAGE_SHA256', value: packageSha256 }
      { name: 'IDENTITY_CLIENT_ID', value: managedIdentityClientId }
      { secureValue: packageBase64, name: 'PACKAGE_BASE64' }
    ]
    scriptContent: '''
      #!/bin/bash
      set -euo pipefail

      ZIP_FILE="/tmp/hybrid-worker-${PACKAGE_VERSION}.zip"
      BLOB_NAME="hybrid-worker-${PACKAGE_VERSION}.zip"

      echo "=== Hybrid Worker Upload Script ==="
      echo "Version: ${PACKAGE_VERSION}"
      echo "Storage: ${STORAGE_ACCOUNT}/${CONTAINER_NAME}"
      echo "Expected SHA256: ${PACKAGE_SHA256}"

      # Decode base64 package
      echo "Decoding base64 package..."
      echo "$PACKAGE_BASE64" | base64 -d > "$ZIP_FILE"
      FILE_SIZE=$(stat -c%s "$ZIP_FILE")
      echo "Decoded file size: ${FILE_SIZE} bytes"

      # Verify SHA256
      ACTUAL_SHA256=$(sha256sum "$ZIP_FILE" | cut -d' ' -f1)
      echo "Actual SHA256:   ${ACTUAL_SHA256}"
      if [ "$ACTUAL_SHA256" != "$PACKAGE_SHA256" ]; then
        echo "ERROR: SHA256 mismatch!"
        exit 1
      fi
      echo "SHA256 verified."

      # Login with managed identity
      az login --identity --username "$IDENTITY_CLIENT_ID" --output none

      # Upload zip package
      echo "Uploading ${BLOB_NAME}..."
      az storage blob upload \
        --account-name "$STORAGE_ACCOUNT" \
        --container-name "$CONTAINER_NAME" \
        --name "$BLOB_NAME" \
        --file "$ZIP_FILE" \
        --overwrite \
        --auth-mode login

      # Upload version manifest
      echo "Uploading version.json..."
      PUBLISH_TIME=$(date -u +%Y-%m-%dT%H:%M:%SZ)
      jq -n \
        --arg v "$PACKAGE_VERSION" \
        --arg h "$PACKAGE_SHA256" \
        --arg f "$BLOB_NAME" \
        --arg t "$PUBLISH_TIME" \
        '{version:$v, sha256:$h, fileName:$f, publishedAt:$t, releaseNotes:"Automated build"}' \
        > /tmp/version.json
      az storage blob upload \
        --account-name "$STORAGE_ACCOUNT" \
        --container-name "$CONTAINER_NAME" \
        --name "version.json" \
        --file "/tmp/version.json" \
        --overwrite \
        --auth-mode login

      echo "=== Upload complete ==="
    '''
  }
}
