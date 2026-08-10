// Commit messages drive the version number and the changelog, so they are linted rather than
// left to habit. See CONTRIBUTING.md.
module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    'scope-enum': [
      2,
      'always',
      [
        'core',      // engine: transfer, detection, naming, journal
        'app',       // WinForms UI
        'transfer',
        'detection',
        'naming',
        'symlink',
        'config',
        'logging',
        'docs',
        'build',
        'ci',
        'deps',
        'release'
      ]
    ],
    // Long explanations belong in the body, and the subject shows up in the changelog.
    'header-max-length': [2, 'always', 100],
    'body-max-line-length': [2, 'always', 100]
  }
};
