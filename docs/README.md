# Releasing Max

Releases start from the commit currently at the tip of `staging`. Choose a new
version and create an annotated `docker/staging/<version>` tag on that commit:

```bash
git fetch origin staging
git tag -a docker/staging/0.0.13 origin/staging -m "Max 0.0.13 candidate"
git push origin docker/staging/0.0.13
```

Replace `0.0.13` with the version being released. Version tags are immutable,
so use a new version if the Docker image tag already exists.

Pushing the tag triggers the **build-image** GitHub Actions workflow. It builds
the tagged commit, publishes the versioned Docker image, and proposes that exact
image for the staging deployment. Wait for the workflow and the staging
deployment to complete, then verify the release candidate in staging.

When the candidate is ready, run the **prepare-release** workflow from the
repository's Actions page. Select one of its approval modes:

- `review` creates a `release/<version>` pull request into `master` and leaves it
  open for review and manual merging.
- `auto` creates the same pull request and merges it automatically once required
  checks allow it.

The workflow reads the image currently deployed in staging, verifies its digest
and source commit, and creates the release pull request from that exact commit.
For `review` mode, review and merge this pull request when it is ready.

Merging the release pull request automatically triggers the
**finalize-release** workflow. It verifies that the staged image matches the
merged release, creates the `v<version>` Git tag on the merge commit, and
proposes the same image digest for production. In `review` mode, review and
merge the resulting production deployment pull request; in `auto` mode, the
production auto-merge approval is carried through by the release pull request.

Confirm that **finalize-release** succeeds and that the production deployment
completes before considering the release finished.
