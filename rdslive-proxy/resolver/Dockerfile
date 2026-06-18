# Playwright base image ships Chromium + all OS deps + Node. Tag MUST match the
# "playwright" npm version in package.json.
FROM mcr.microsoft.com/playwright:v1.48.0-jammy

WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev
# Install REAL Google Chrome (channel 'chrome') — it has the H.264/AAC codecs the
# player requires; Playwright's bundled Chromium does not, so the player refuses to
# play and never fetches the stream.
RUN npx playwright install chrome
COPY index.js ./

# Run as the image's non-root 'pwuser'. Start Xvfb (virtual display) so Chrome runs
# headed, then exec node so logs flow to `docker logs`.
USER pwuser
CMD ["bash", "-c", "Xvfb :99 -screen 0 1280x720x24 -nolisten tcp >/tmp/xvfb.log 2>&1 & export DISPLAY=:99; sleep 2; exec node index.js"]
