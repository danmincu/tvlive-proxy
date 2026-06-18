# Playwright base image ships Chromium + all OS deps + Node. Tag MUST match the
# "playwright" npm version in package.json so the bundled browser is found.
FROM mcr.microsoft.com/playwright:v1.48.0-jammy

WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev
COPY index.js ./

# Run as the image's non-root 'pwuser'.
USER pwuser
CMD ["node", "index.js"]
