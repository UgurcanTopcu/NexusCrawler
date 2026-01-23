/**
 * Scrapper - Common JavaScript Utilities
 * Shared across all scraper pages
 */

// State management
let abortController = null;
let isRunning = false;
let sessionId = null;

/**
 * Initialize SSE stream processing for a scraping operation
 * @param {string} url - API endpoint URL
 * @param {Object} options - Fetch options (method, headers, body)
 * @param {Object} callbacks - UI callback functions
 */
async function startSseStream(url, options, callbacks) {
    const {
        onStart,
        onProgress,
        onMessage,
        onDownload,
        onComplete,
        onError,
        onStop
    } = callbacks;

    sessionId = Date.now().toString();
    abortController = new AbortController();
    isRunning = true;

    if (onStart) onStart();

    try {
        const response = await fetch(url, {
            ...options,
            signal: abortController.signal
        });

        if (!response.ok) throw new Error('Request failed');

        const reader = response.body.getReader();
        const decoder = new TextDecoder();

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            const text = decoder.decode(value);
            const lines = text.split('\n');

            for (const line of lines) {
                if (line.startsWith('data: ')) {
                    try {
                        const data = JSON.parse(line.substring(6));

                        if (data.progress !== undefined && onProgress) {
                            onProgress(data.progress);
                        }

                        if (data.message && onMessage) {
                            onMessage(data.message, data.type);
                        }

                        if (data.downloadUrl && onDownload) {
                            onDownload(data.downloadUrl, data.fileName);
                        }

                        if (data.complete) {
                            isRunning = false;
                            if (onComplete) onComplete();
                        }
                    } catch (parseErr) {
                        // Ignore parse errors for incomplete chunks
                    }
                }
            }
        }
    } catch (error) {
        if (error.name !== 'AbortError') {
            isRunning = false;
            if (onError) onError(error.message);
        }
    }
}

/**
 * Stop the current operation
 * @param {string} stopUrl - API endpoint to send stop signal
 */
async function stopOperation(stopUrl) {
    if (sessionId) {
        try {
            await fetch(stopUrl + sessionId, { method: 'POST' });
        } catch (e) {
            console.log('Stop request failed:', e);
        }
    }
    if (abortController) {
        abortController.abort();
    }
    isRunning = false;
}

/**
 * Get the current session ID
 */
function getSessionId() {
    return sessionId;
}

/**
 * Check if an operation is currently running
 */
function getIsRunning() {
    return isRunning;
}

/**
 * Update progress bar UI
 * @param {number} progress - Progress percentage (0-100)
 */
function updateProgressBar(progress) {
    const progressFill = document.getElementById('progressFill');
    if (progressFill) {
        progressFill.style.width = progress + '%';
        progressFill.textContent = progress + '%';
    }
}

/**
 * Add a status message to the status container
 * @param {string} message - Message text
 * @param {string} type - Message type: 'success', 'error', 'warning', or 'info'
 */
function addStatusMessage(message, type) {
    const status = document.getElementById('status');
    if (status) {
        const statusItem = document.createElement('div');
        statusItem.className = 'status-item';
        if (type === 'success') statusItem.className += ' success';
        else if (type === 'error') statusItem.className += ' error';
        else if (type === 'warning') statusItem.className += ' warning';
        statusItem.textContent = message;
        status.insertBefore(statusItem, status.firstChild);
    }
}

/**
 * Add a download link to the status container
 * @param {string} downloadUrl - URL to download file
 * @param {string} fileName - Name of the file
 */
function addDownloadLink(downloadUrl, fileName) {
    const status = document.getElementById('status');
    if (status) {
        const link = document.createElement('a');
        link.href = downloadUrl;
        link.className = 'download-link';
        link.textContent = '📥 Download Results';
        link.download = fileName;
        status.insertBefore(link, status.firstChild);
    }
}

/**
 * Show/hide progress section
 * @param {boolean} show - Whether to show the progress section
 */
function showProgress(show) {
    const progress = document.getElementById('progress');
    if (progress) {
        progress.style.display = show ? 'block' : 'none';
    }
}

/**
 * Reset progress UI
 */
function resetProgressUI() {
    const progressFill = document.getElementById('progressFill');
    const status = document.getElementById('status');
    
    if (progressFill) {
        progressFill.style.width = '0%';
        progressFill.textContent = '0%';
    }
    if (status) {
        status.innerHTML = '';
    }
    showProgress(false);
}

/**
 * Setup file upload drag and drop handlers
 * @param {string} dropZoneId - ID of the drop zone element
 * @param {string} fileInputId - ID of the file input element
 * @param {string} fileNameId - ID of the filename display element
 * @param {Function} onFileSelected - Callback when file is selected
 */
function setupFileUpload(dropZoneId, fileInputId, fileNameId, onFileSelected) {
    const dropZone = document.getElementById(dropZoneId);
    const fileInput = document.getElementById(fileInputId);
    const fileName = document.getElementById(fileNameId);

    if (!dropZone || !fileInput) return;

    dropZone.addEventListener('click', () => fileInput.click());
    
    dropZone.addEventListener('dragover', (e) => {
        e.preventDefault();
        dropZone.classList.add('dragover');
    });
    
    dropZone.addEventListener('dragleave', () => {
        dropZone.classList.remove('dragover');
    });
    
    dropZone.addEventListener('drop', (e) => {
        e.preventDefault();
        dropZone.classList.remove('dragover');
        if (e.dataTransfer.files.length > 0) {
            handleFileSelection(e.dataTransfer.files[0], fileName, onFileSelected);
        }
    });
    
    fileInput.addEventListener('change', (e) => {
        if (e.target.files.length > 0) {
            handleFileSelection(e.target.files[0], fileName, onFileSelected);
        }
    });
}

/**
 * Handle file selection
 */
function handleFileSelection(file, fileNameElement, callback) {
    if (!file.name.match(/\.(xlsx|xls)$/i)) {
        alert('Please upload an Excel file (.xlsx or .xls)');
        return;
    }
    if (fileNameElement) {
        fileNameElement.textContent = '✓ ' + file.name;
    }
    if (callback) {
        callback(file);
    }
}

// Navigation helper
function navigateTo(url) {
    window.location.href = url;
}
