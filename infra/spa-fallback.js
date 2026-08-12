// SPA route fallback for the S3 origin only. Ordered behaviors send /api/*
// and /hubs/* to the ALB before this function runs; the guards are belt and
// braces. Anything with a file extension (assets) passes through untouched.
function handler(event) {
    var request = event.request;
    var uri = request.uri;
    if (!uri.startsWith('/api/') && !uri.startsWith('/hubs/') && !uri.includes('.')) {
        request.uri = '/index.html';
    }
    return request;
}
