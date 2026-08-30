#!/usr/bin/env python3
import json
import os
import re
import subprocess
import sys


provider = os.environ["PROVIDER"]
prefix = os.environ["PREFIX"]
bucket = os.environ.get("BUCKET", "")
region = os.environ.get("REGION", "")
endpoint = os.environ.get("ENDPOINT", "")
azure_account = os.environ.get("AZURE_ACCOUNT", "")
azure_container = os.environ.get("AZURE_CONTAINER", "")
scope = f"{prefix}/"

if provider not in {"aws", "azure", "r2", "b2"}:
    raise SystemExit("cleanup refused an unknown provider")
if re.fullmatch(
    rf"vistara-live/{provider}/[1-9][0-9]*-[1-9][0-9]*-[0-9a-f]{{16}}",
    prefix,
) is None:
    raise SystemExit("cleanup refused a prefix outside the live provider scope")


def run(command):
    return subprocess.run(
        command,
        check=True,
        capture_output=True,
        text=True,
    ).stdout


deleted = 0
for _ in range(100):
    if provider == "azure":
        output = run(
            [
                "az",
                "storage",
                "blob",
                "list",
                "--auth-mode",
                "login",
                "--account-name",
                azure_account,
                "--container-name",
                azure_container,
                "--prefix",
                scope,
                "--num-results",
                "5000",
                "--query",
                "[].name",
                "--output",
                "json",
                "--only-show-errors",
            ]
        )
        keys = json.loads(output)
    else:
        command = [
            "aws",
            "s3api",
            "list-objects-v2",
            "--bucket",
            bucket,
            "--prefix",
            scope,
            "--max-keys",
            "1000",
            "--query",
            "Contents[].Key",
            "--output",
            "json",
            "--region",
            region,
            "--no-cli-pager",
        ]
        if endpoint:
            command.extend(["--endpoint-url", endpoint])
        keys = json.loads(run(command))

    if not keys:
        break
    if not isinstance(keys, list) or any(
        not isinstance(key, str) or not key.startswith(scope) for key in keys
    ):
        raise SystemExit("cleanup refused a provider result outside the run prefix")

    if provider == "azure":
        for key in keys:
            run(
                [
                    "az",
                    "storage",
                    "blob",
                    "delete",
                    "--auth-mode",
                    "login",
                    "--account-name",
                    azure_account,
                    "--container-name",
                    azure_container,
                    "--name",
                    key,
                    "--output",
                    "none",
                    "--only-show-errors",
                ]
            )
    else:
        delete_request = json.dumps(
            {
                "Objects": [{"Key": key} for key in keys],
                "Quiet": True,
            },
            separators=(",", ":"),
        )
        command = [
            "aws",
            "s3api",
            "delete-objects",
            "--bucket",
            bucket,
            "--delete",
            delete_request,
            "--region",
            region,
            "--output",
            "json",
            "--no-cli-pager",
        ]
        if endpoint:
            command.extend(["--endpoint-url", endpoint])
        run(command)
    deleted += len(keys)
else:
    raise SystemExit("cleanup stopped after 100 bounded object batches")

aborted = 0
if provider != "azure":
    for _ in range(100):
        command = [
            "aws",
            "s3api",
            "list-multipart-uploads",
            "--bucket",
            bucket,
            "--prefix",
            scope,
            "--max-uploads",
            "1000",
            "--query",
            "Uploads[].{Key:Key,UploadId:UploadId}",
            "--output",
            "json",
            "--region",
            region,
            "--no-cli-pager",
        ]
        if endpoint:
            command.extend(["--endpoint-url", endpoint])
        uploads = json.loads(run(command))
        if not uploads:
            break
        if not isinstance(uploads, list) or any(
            not isinstance(upload, dict)
            or not isinstance(upload.get("Key"), str)
            or not upload["Key"].startswith(scope)
            or not isinstance(upload.get("UploadId"), str)
            for upload in uploads
        ):
            raise SystemExit("cleanup refused a multipart upload outside the run prefix")
        for upload in uploads:
            command = [
                "aws",
                "s3api",
                "abort-multipart-upload",
                "--bucket",
                bucket,
                "--key",
                upload["Key"],
                "--upload-id",
                upload["UploadId"],
                "--region",
                region,
                "--no-cli-pager",
            ]
            if endpoint:
                command.extend(["--endpoint-url", endpoint])
            run(command)
        aborted += len(uploads)
    else:
        raise SystemExit("cleanup stopped after 100 bounded multipart batches")

print(
    f"Prefix-bounded cleanup removed {deleted} object(s) "
    f"and aborted {aborted} multipart upload(s) for {provider}."
)
