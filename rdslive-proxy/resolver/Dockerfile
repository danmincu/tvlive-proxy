# Playwright base image ships Chromium + all OS deps + Node. Tag MUST match the
# "playwright" npm version in package.json so the bundled browser is found.
FROM mcr.microsoft.com/playwright:v1.48.0-jammy

WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev
COPY index.js ./

# Run as the image's non-root 'pwuser'. Start Xvfb (a virtual display) so Chromium
# can run HEADED — protected players (disable-devtool / Clappr) block headless — then
# exec node so its logs flow straight to `docker logs` (xvfb-run was swallowing them).
USER pwuser
CMD ["bash", "-c", "Xvfb :99 -screen 0 1280x720x24 -nolisten tcp >/tmp/xvfb.log 2>&1 & export DISPLAY=:99; sleep 2; exec node index.js"]
